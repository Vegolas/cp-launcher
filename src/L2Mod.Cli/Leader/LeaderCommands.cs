using System.Text;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Cli.Leader;

/// <summary>Default locations of the leader's files; tests pass temporary ones.</summary>
public sealed record LeaderDefaults(string PrivateKeyPath, string PublicKeyPath, string PreviewDirectory)
{
    /// <summary>Private key in %USERPROFILE%\.l2mod, public key and previews relative to the current folder (the repo root).</summary>
    public static LeaderDefaults ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LeaderDefaults(
            Path.Combine(profile, ".l2mod", "signing-private.pem"),
            Path.GetFullPath(Path.Combine("keys", "signing-public.pem")),
            Path.GetFullPath(Path.Combine("work", "preview", "packs")));
    }
}

/// <summary>
/// The CP leader's commands: keygen, build, sign, verify and publish (a dry run until hosting is chosen).
/// Bad input throws (the CLI prints it); a failed check prints the reasons and returns <see cref="ExitFailed"/>.
/// </summary>
public sealed class LeaderCommands
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    private const int DefaultPreviewScale = 4;
    private const string DefaultBucket = "<bucket>";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly LeaderDefaults _defaults;

    public LeaderCommands(TextWriter output, TextWriter error, LeaderDefaults defaults)
    {
        _output = output;
        _error = error;
        _defaults = defaults;
    }

    public static IReadOnlyList<string> Names
    {
        get
        {
            return new[] { "keygen", "build", "sign", "verify", "publish" };
        }
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("  keygen  [--private <path>] [--public <path>]");
        output.WriteLine("          default private key %USERPROFILE%\\.l2mod\\signing-private.pem (never overwritten), public key keys/signing-public.pem");
        output.WriteLine("  build   <recipes-dir> <original Icon.utx> <out-dir> [--version V] [--previews <dir>] [--preview-scale 4] [--overwrite]");
        output.WriteLine("          default previews work/preview/packs (they show NCSoft icons: never inside out-dir, never published)");
        output.WriteLine("  sign    <dist-dir> [--key <private.pem>] [--public <public.pem>]");
        output.WriteLine("  verify  <dist-dir> [--public <public.pem>]");
        output.WriteLine("  publish <dist-dir> [--public <public.pem>] [--bucket <name>]   (dry run: prints the upload plan, uploads nothing)");
    }

    public int Run(string name, IReadOnlyList<string> arguments)
    {
        switch (name)
        {
            case "keygen":
                return Keygen(arguments);
            case "build":
                return Build(arguments);
            case "sign":
                return Sign(arguments);
            case "verify":
                return Verify(arguments);
            case "publish":
                return Publish(arguments);
            default:
                throw new ArgumentException($"Unknown leader command '{name}'.");
        }
    }

    public int Keygen(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "private", "public" }, Array.Empty<string>());
        if (command.Positional.Count != 0)
        {
            return Usage();
        }

        string privatePath = Path.GetFullPath(command.GetOption("private") ?? _defaults.PrivateKeyPath);
        string publicPath = Path.GetFullPath(command.GetOption("public") ?? _defaults.PublicKeyPath);
        if (string.Equals(privatePath, publicPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The private and the public key need different files.");
        }

        if (File.Exists(privatePath))
        {
            throw new IOException($"{privatePath} already exists. keygen never overwrites a private key: launchers trust its public half. Pass --private to create a different one.");
        }

        if (File.Exists(publicPath))
        {
            throw new IOException($"{publicPath} already exists. Delete it yourself only if you really mean to replace the key the launchers trust.");
        }

        string? repository = FindGitWorkTree(privatePath);
        if (repository is not null)
        {
            throw new IOException($"{privatePath} is inside the git working tree {repository}. Keep the private key outside any repository (default %USERPROFILE%\\.l2mod).");
        }

        SigningKeyPair pair = ManifestSignature.GenerateKeyPair();
        WriteNewFile(privatePath, pair.PrivateKeyPem);
        try
        {
            WriteNewFile(publicPath, pair.PublicKeyPem);
        }
        catch
        {
            File.Delete(privatePath);
            throw;
        }

        _output.WriteLine($"Private key: {privatePath}");
        _output.WriteLine("  keep it outside the repo and store a copy in your password manager; losing it means sending every member a new launcher.");
        _output.WriteLine($"Public key:  {publicPath}");
        _output.WriteLine("  commit it; the launcher embeds it.");
        _output.WriteLine($"Fingerprint: {SigningKeys.GetFingerprint(pair.PublicKeyPem)}");
        return ExitOk;
    }

    public int Build(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "version", "previews", "preview-scale" }, new[] { "overwrite" });
        if (command.Positional.Count != 3)
        {
            return Usage();
        }

        string originalPath = Path.GetFullPath(command.Positional[1]);
        string outputPath = Path.GetFullPath(command.Positional[2]);
        string previewPath = Path.GetFullPath(command.GetOption("previews") ?? _defaults.PreviewDirectory);
        if (IsSameOrInside(previewPath, outputPath))
        {
            throw new ArgumentException($"Preview sheets show NCSoft icons and must not go into the upload folder {outputPath}; pass --previews outside it.");
        }

        if (IsSameOrInside(outputPath, previewPath))
        {
            throw new ArgumentException($"The output folder {outputPath} must not be inside the preview folder {previewPath}.");
        }

        if (IsSameOrInside(originalPath, outputPath))
        {
            throw new ArgumentException($"The output folder {outputPath} contains the original {originalPath}; choose a separate folder such as dist.");
        }

        RecipeProject project = RecipeProject.Load(command.Positional[0]);
        var options = new DistBuildOptions
        {
            Version = command.GetOption("version"),
            PreviewScale = command.GetInt("preview-scale", DefaultPreviewScale, 1, 16),
        };
        DistBuildResult build = DistBuilder.Build(project, originalPath, options);
        DistWriteResult written = DistWriter.Write(outputPath, build, command.HasFlag("overwrite"));
        WritePreviews(previewPath, build.Previews);

        _output.WriteLine($"Version {build.Manifest.Version}, enabled {build.Manifest.Enabled.ToString().ToLowerInvariant()}, original Icon.utx {build.OriginalSha256}.");
        foreach (CompiledPack pack in build.Packs)
        {
            string type = pack.Recipe.Type.ToString().ToLowerInvariant();
            string selection = pack.Recipe.Default ? "on by default" : "off by default";
            _output.WriteLine($"  {pack.Recipe.Id,-20} {type,-7} {pack.Entries.Count,4} operation(s), {selection}; preview {Path.Combine(previewPath, pack.Recipe.Id + ".png")}");
        }

        if (build.Manifest.Announcements.Count > 0 || build.Manifest.OptionPresets.Count > 0)
        {
            _output.WriteLine($"  {build.Manifest.Announcements.Count} announcement(s), {build.Manifest.OptionPresets.Count} Option.ini preset(s).");
        }

        foreach (string warning in build.Warnings.Concat(written.Warnings))
        {
            _output.WriteLine($"  warning: {warning}");
        }

        if (written.Removed.Count > 0)
        {
            _output.WriteLine($"  replaced the previous build ({written.Removed.Count} file(s), including any old signature).");
        }

        _output.WriteLine($"Wrote {outputPath}: {DistLayout.ManifestFile} ({build.ManifestBytes.Length} bytes) and {build.Files.Count} other file(s). Not signed yet.");
        _output.WriteLine("Next: review the preview sheets, then run sign.");
        return ExitOk;
    }

    public int Sign(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "key", "public" }, Array.Empty<string>());
        if (command.Positional.Count != 1)
        {
            return Usage();
        }

        string distPath = Path.GetFullPath(command.Positional[0]);
        DistVerification check = DistVerifier.Verify(distPath, null);
        if (!check.IsValid)
        {
            return Fail(check.Errors, "Not signed.");
        }

        string keyPath = Path.GetFullPath(command.GetOption("key") ?? _defaults.PrivateKeyPath);
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException($"Private key {keyPath} not found. Create one with keygen or pass --key.", keyPath);
        }

        string privatePem = File.ReadAllText(keyPath);
        string? publicPath = ResolvePublicKey(command.GetOption("public"), false);
        string manifestPath = Path.Combine(distPath, DistLayout.ManifestFile);
        byte[] manifest = File.ReadAllBytes(manifestPath);
        byte[] signature = ManifestSignature.Sign(manifest, privatePem);
        string fingerprint = SigningKeys.GetFingerprint(privatePem);
        if (publicPath is null)
        {
            _output.WriteLine($"  warning: no public key at {_defaults.PublicKeyPath}; cannot check that this key is the one the launcher trusts.");
        }
        else
        {
            string publicPem = File.ReadAllText(publicPath);
            if (!ManifestSignature.Verify(manifest, signature, publicPem))
            {
                string message = $"The private key {keyPath} (fingerprint {fingerprint}) does not belong to the public key {publicPath} (fingerprint {SigningKeys.GetFingerprint(publicPem)}).";
                return Fail(new[] { message }, "Not signed.");
            }
        }

        File.WriteAllBytes(Path.Combine(distPath, DistLayout.SignatureFile), signature);
        _output.WriteLine($"Signed {manifestPath} (version {check.Manifest!.Version}) -> {DistLayout.SignatureFile}, key fingerprint {fingerprint}.");
        return ExitOk;
    }

    public int Verify(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "public" }, Array.Empty<string>());
        if (command.Positional.Count != 1)
        {
            return Usage();
        }

        string distPath = Path.GetFullPath(command.Positional[0]);
        string publicPem = File.ReadAllText(ResolvePublicKey(command.GetOption("public"), true)!);
        DistVerification result = DistVerifier.Verify(distPath, publicPem);
        if (!result.IsValid)
        {
            return Fail(result.Errors, "Verification FAILED.");
        }

        Manifest manifest = result.Manifest!;
        int operations = manifest.Packs.Sum(pack => pack.Operations.Count);
        _output.WriteLine($"OK: {distPath} version {manifest.Version}, enabled {manifest.Enabled.ToString().ToLowerInvariant()}, signed by {SigningKeys.GetFingerprint(publicPem)}.");
        _output.WriteLine($"  {manifest.Packs.Count} pack(s), {operations} operation(s), {result.ReferencedFiles.Count} referenced file(s) checked.");
        foreach (Pack pack in manifest.Packs)
        {
            _output.WriteLine($"  {pack.Id,-20} {pack.Operations.Count,4} operation(s)");
        }

        foreach (string file in FindUnreferenced(distPath, result))
        {
            _output.WriteLine($"  warning: {file} is not referenced by the manifest and will not be uploaded.");
        }

        return ExitOk;
    }

    public int Publish(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "public", "bucket" }, Array.Empty<string>());
        if (command.Positional.Count != 1)
        {
            return Usage();
        }

        string distPath = Path.GetFullPath(command.Positional[0]);
        string publicPem = File.ReadAllText(ResolvePublicKey(command.GetOption("public"), true)!);
        DistVerification result = DistVerifier.Verify(distPath, publicPem);
        if (!result.IsValid)
        {
            return Fail(result.Errors, "Nothing published.");
        }

        string bucket = command.GetOption("bucket") ?? DefaultBucket;
        IReadOnlyList<UploadStep> steps = UploadPlan.Create(result);
        _output.WriteLine("DRY RUN: hosting is not chosen yet (likely Cloudflare R2 via wrangler). Nothing was uploaded.");
        _output.WriteLine($"Verified {distPath}: version {result.Manifest!.Version}, signed by {SigningKeys.GetFingerprint(publicPem)}.");
        _output.WriteLine("Planned upload, in this order (manifest last, so members never see it before its files):");
        for (int i = 0; i < steps.Count; i++)
        {
            _output.WriteLine($"  {i + 1,3}. {steps[i].Path}  [{steps[i].ContentType}; Cache-Control: {steps[i].CacheControl}]");
        }

        _output.WriteLine("Equivalent wrangler commands:");
        foreach (UploadStep step in steps)
        {
            _output.WriteLine($"  {UploadPlan.ToWranglerCommand(step, distPath, bucket)}");
        }

        foreach (string file in FindUnreferenced(distPath, result))
        {
            _output.WriteLine($"  skipped: {file} (not referenced by the manifest)");
        }

        return ExitOk;
    }

    /// <summary>The nearest folder at or above <paramref name="path"/> that holds a .git folder or file, or null.</summary>
    public static string? FindGitWorkTree(string path)
    {
        DirectoryInfo? directory = new FileInfo(Path.GetFullPath(path)).Directory;
        while (directory is not null)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private string? ResolvePublicKey(string? option, bool required)
    {
        if (option is not null)
        {
            string path = Path.GetFullPath(option);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Public key {path} not found.", path);
            }

            return path;
        }

        if (File.Exists(_defaults.PublicKeyPath))
        {
            return _defaults.PublicKeyPath;
        }

        if (required)
        {
            throw new FileNotFoundException($"Public key {_defaults.PublicKeyPath} not found. Pass --public.", _defaults.PublicKeyPath);
        }

        return null;
    }

    private static IEnumerable<string> FindUnreferenced(string distPath, DistVerification result)
    {
        foreach (string file in DistWriter.ListFiles(distPath))
        {
            if (file == DistLayout.ManifestFile || file == DistLayout.SignatureFile || result.ReferencedFiles.Contains(file, StringComparer.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    private void WritePreviews(string directory, IReadOnlyList<PackPreview> previews)
    {
        Directory.CreateDirectory(directory);
        foreach (PackPreview preview in previews)
        {
            PngCodec.Save(preview.Sheet, Path.Combine(directory, preview.PackId + ".png"));
            var lines = new List<string>
            {
                $"# {preview.PackId}: before | after (after decoded from the encoded file), numbered left to right, top to bottom.",
            };
            lines.AddRange(preview.Index);
            File.WriteAllLines(Path.Combine(directory, preview.PackId + ".txt"), lines);
        }
    }

    private static void WriteNewFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes);
    }

    private static bool IsSameOrInside(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private int Fail(IEnumerable<string> errors, string summary)
    {
        foreach (string error in errors)
        {
            _error.WriteLine($"  {error}");
        }

        _error.WriteLine(summary);
        return ExitFailed;
    }

    private int Usage()
    {
        PrintUsage(_error);
        return ExitUsage;
    }
}

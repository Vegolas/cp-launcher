using System.Text;
using L2Mod.Core;
using L2Mod.Core.Imaging;
using L2Mod.Core.Manifests;
using L2Mod.Core.Publishing;
using L2Mod.Core.Signing;

namespace L2Mod.Cli.Leader;

/// <summary>Default locations of the leader's files; tests pass temporary ones.</summary>
public sealed record LeaderDefaults(string PrivateKeyPath, string PublicKeyPath, string PreviewDirectory)
{
    public const string DefaultRepository = "Vegolas/cp-launcher";

    /// <summary>The public key the launcher embeds; publish refuses a dist signed for another key. Null skips the check.</summary>
    public string? LauncherPublicKeyPath { get; init; }

    /// <summary>The GitHub repository ("owner/name") whose releases and Pages carry the dist.</summary>
    public string Repository { get; init; } = DefaultRepository;

    /// <summary>Private key in %USERPROFILE%\.l2mod, public keys and previews relative to the current folder (the repo root).</summary>
    public static LeaderDefaults ForCurrentUser()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LeaderDefaults(
            Path.Combine(profile, ".l2mod", "signing-private.pem"),
            Path.GetFullPath(Path.Combine("keys", "signing-public.pem")),
            Path.GetFullPath(Path.Combine("work", "preview", "packs")))
        {
            LauncherPublicKeyPath = Path.GetFullPath(Path.Combine("src", "CpLauncher", "Keys", "manifest-public.pem")),
        };
    }
}

/// <summary>
/// The CP leader's commands: keygen, build, sign, verify, publish (a GitHub release plus the Pages deployment), and
/// stage-pages, which the Pages workflow runs. Bad input throws (the CLI prints it); a failed check prints the
/// reasons and returns <see cref="ExitFailed"/>.
/// </summary>
public sealed class LeaderCommands
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    private const int DefaultPreviewScale = 4;
    private const string GitHubCli = "gh";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly LeaderDefaults _defaults;
    private readonly ICommandRunner _runner;

    public LeaderCommands(TextWriter output, TextWriter error, LeaderDefaults defaults)
        : this(output, error, defaults, new ProcessCommandRunner())
    {
    }

    public LeaderCommands(TextWriter output, TextWriter error, LeaderDefaults defaults, ICommandRunner runner)
    {
        _output = output;
        _error = error;
        _defaults = defaults;
        _runner = runner;
    }

    public static IReadOnlyList<string> Names
    {
        get
        {
            return new[] { "keygen", "build", "sign", "verify", "publish", "stage-pages" };
        }
    }

    public static void PrintUsage(TextWriter output)
    {
        output.WriteLine("  keygen  [--private <path>] [--public <path>]");
        output.WriteLine("          default private key %USERPROFILE%\\.l2mod\\signing-private.pem (never overwritten), public key keys/signing-public.pem");
        output.WriteLine("  build   <recipes-dir> <original Icon.utx | dir with the originals> <out-dir> [--version V] [--previews <dir>] [--preview-scale 4] [--overwrite]");
        output.WriteLine("          effect packs also need the original LineageSkillEffect.u / LineageEffect.u: pass a folder holding all originals");
        output.WriteLine("          default previews work/preview/packs (they show NCSoft icons: never inside out-dir, never published)");
        output.WriteLine("  sign    <dist-dir> [--key <private.pem>] [--public <public.pem>]");
        output.WriteLine("  verify  <dist-dir> [--public <public.pem>]");
        output.WriteLine("  publish <dist-dir> [--public <public.pem>] [--repo owner/name] [--dry-run]");
        output.WriteLine("          uploads dist-<version>.zip as a GitHub release and starts the Pages deployment (needs gh, logged in)");
        output.WriteLine("  stage-pages <dist.zip|dist-dir> --out <site-dir> [--public <public.pem>] [--previous <dist.zip|dist-dir>]");
        output.WriteLine("          run by .github/workflows/pages.yml: verifies a published dist and writes the site, keeping the previous version's files");
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
            case "stage-pages":
                return StagePages(arguments);
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
        IReadOnlyList<string> originalPaths = new[] { originalPath };
        if (Directory.Exists(originalPath))
        {
            originalPaths = ClientFiles.Patchable
                .Select(file => Path.Combine(originalPath, file))
                .Where(File.Exists)
                .ToList();
        }

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
        DistBuildResult build = DistBuilder.Build(project, originalPaths, options);
        DistWriteResult written = DistWriter.Write(outputPath, build, command.HasFlag("overwrite"));
        WritePreviews(previewPath, build.Previews);

        _output.WriteLine($"Version {build.Manifest.Version}, enabled {build.Manifest.Enabled.ToString().ToLowerInvariant()}.");
        foreach (KeyValuePair<string, string> original in build.Originals)
        {
            _output.WriteLine($"  original {original.Key} {original.Value}");
        }

        foreach (CompiledPack pack in build.Packs)
        {
            string type = RecipePack.GetTypeName(pack.Recipe.Type);
            string selection = pack.Recipe.Default ? "on by default" : "off by default";
            string optionCount = string.Empty;
            if (pack.Options.Count > 0)
            {
                optionCount = $" in {pack.Options.Count} option(s)";
            }

            string preview = "no preview";
            if (build.Previews.Any(item => item.PackId == pack.Recipe.Id))
            {
                preview = "preview " + Path.Combine(previewPath, pack.Recipe.Id + ".png");
            }

            _output.WriteLine($"  {pack.Recipe.Id,-20} {type,-16} {pack.Entries.Count,4} operation(s){optionCount}, {selection}; {preview}");
        }

        foreach (string note in build.Notes)
        {
            _output.WriteLine($"  {note}");
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

        foreach (string file in DistVerifier.FindUnreferenced(distPath, result))
        {
            _output.WriteLine($"  warning: {file} is not referenced by the manifest and will not be published.");
        }

        return ExitOk;
    }

    /// <summary>
    /// Verifies the dist, uploads it as the release dist-&lt;version&gt; (one zip, never replaced) and starts the
    /// Pages workflow, which verifies it again against the key the launcher embeds and deploys it. The private key
    /// never leaves this computer.
    /// </summary>
    public int Publish(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "public", "repo" }, new[] { "dry-run" });
        if (command.Positional.Count != 1)
        {
            return Usage();
        }

        PagesTarget target = PagesTarget.Parse(command.GetOption("repo") ?? _defaults.Repository);
        string distPath = Path.GetFullPath(command.Positional[0]);
        string publicPem = File.ReadAllText(ResolvePublicKey(command.GetOption("public"), true)!);
        DistVerification result = DistVerifier.Verify(distPath, publicPem);
        if (!result.IsValid)
        {
            return Fail(result.Errors, "Nothing published.");
        }

        string? keyProblem = CheckLauncherKey(publicPem);
        if (keyProblem is not null)
        {
            return Fail(new[] { keyProblem }, "Nothing published.");
        }

        Manifest manifest = result.Manifest!;
        string tag = PagesTarget.GetTag(manifest.Version);
        _output.WriteLine($"Verified {distPath}: version {manifest.Version}, signed by {SigningKeys.GetFingerprint(publicPem)}.");
        foreach (string file in DistVerifier.FindUnreferenced(distPath, result))
        {
            _output.WriteLine($"  skipped: {file} (not referenced by the manifest)");
        }

        string[] deploy = { "workflow", "run", PagesTarget.Workflow, "--repo", target.Repository, "--ref", "main", "-f", "tag=" + tag };
        if (command.HasFlag("dry-run"))
        {
            _output.WriteLine($"DRY RUN: nothing uploaded. Would archive {result.ReferencedFiles.Count + 2} file(s) into {tag}.zip and run:");
            _output.WriteLine($"  {GitHubCli} {FormatArguments(ReleaseArguments(target, manifest, tag + ".zip", "notes.md"))}");
            _output.WriteLine($"  {GitHubCli} {FormatArguments(deploy)}");
            return ExitOk;
        }

        string scratch = NewScratchPath("l2mod-publish-");
        try
        {
            string zipPath = Path.Combine(scratch, tag + ".zip");
            string notesPath = Path.Combine(scratch, "notes.md");
            IReadOnlyList<string> entries = DistArchive.Create(distPath, result, zipPath);
            File.WriteAllText(notesPath, BuildReleaseNotes(manifest, target), new UTF8Encoding(false));
            _output.WriteLine($"Archived {entries.Count} file(s) into {tag}.zip ({new FileInfo(zipPath).Length} bytes).");
            if (_runner.Run(GitHubCli, ReleaseArguments(target, manifest, zipPath, notesPath)) != 0)
            {
                string message = $"gh release create failed. If {tag} already exists, that version is published: bump \"version\" in recipes.json and rebuild (a published version is never replaced).";
                return Fail(new[] { message }, "Nothing published.");
            }

            if (_runner.Run(GitHubCli, deploy) != 0)
            {
                string message = $"The release {tag} exists, but the deployment did not start. Start it with: {GitHubCli} {FormatArguments(deploy)}";
                return Fail(new[] { message }, "Not deployed.");
            }
        }
        finally
        {
            DeleteScratch(scratch);
        }

        _output.WriteLine($"Deployment started: https://github.com/{target.Repository}/actions/workflows/{PagesTarget.Workflow}");
        _output.WriteLine($"After a green run, launchers get it from {target.SiteUri}{DistLayout.ManifestFile} within about 10 minutes (GitHub Pages caches for 600 s).");
        return ExitOk;
    }

    /// <summary>
    /// The Pages workflow's step: the published dist (archive or folder) verified against the public key, written
    /// to an empty site folder together with the files of the previous version (the one live until now).
    /// </summary>
    public int StagePages(IReadOnlyList<string> arguments)
    {
        CommandLine command = CommandLine.Parse(arguments, new[] { "public", "previous", "out" }, Array.Empty<string>());
        string? sitePath = command.GetOption("out");
        if (command.Positional.Count != 1 || sitePath is null)
        {
            return Usage();
        }

        string publicPem = File.ReadAllText(ResolvePublicKey(command.GetOption("public"), true)!);
        var scratch = new List<string>();
        try
        {
            string distPath = OpenDist(command.Positional[0], scratch);
            string? previousOption = command.GetOption("previous");
            string? previousPath = null;
            if (previousOption is not null)
            {
                previousPath = OpenDist(previousOption, scratch);
            }

            string site = Path.GetFullPath(sitePath);
            PagesStageResult result = PagesSite.Stage(distPath, publicPem, previousPath, site);
            foreach (string warning in result.Warnings)
            {
                _output.WriteLine($"  warning: {warning}");
            }

            if (!result.IsValid)
            {
                return Fail(result.Errors, "Site not staged.");
            }

            _output.WriteLine($"Staged {site}: version {result.Manifest!.Version}, signed by {SigningKeys.GetFingerprint(publicPem)}.");
            _output.WriteLine($"  {result.Files.Count} file(s) of this version, {result.KeptFromPrevious.Count} kept from the previous one.");
            return ExitOk;
        }
        finally
        {
            foreach (string directory in scratch)
            {
                DeleteScratch(directory);
            }
        }
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

    /// <summary>Null when the launcher embeds the key the dist is signed for (or there is no launcher key to check).</summary>
    private string? CheckLauncherKey(string publicPem)
    {
        string? path = _defaults.LauncherPublicKeyPath;
        if (path is null)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            _output.WriteLine($"  warning: {path} not found; cannot check that the launcher trusts this key (the Pages deployment checks it).");
            return null;
        }

        string launcherFingerprint = SigningKeys.GetFingerprint(File.ReadAllText(path));
        string fingerprint = SigningKeys.GetFingerprint(publicPem);
        if (launcherFingerprint == fingerprint)
        {
            return null;
        }

        return $"The launcher embeds another public key ({path}, fingerprint {launcherFingerprint}) than the one this dist is signed for ({fingerprint}). "
            + "The Pages deployment would refuse it and launchers would reject it: put the public key into the launcher and release that launcher first.";
    }

    private static string[] ReleaseArguments(PagesTarget target, Manifest manifest, string zipPath, string notesPath)
    {
        string tag = PagesTarget.GetTag(manifest.Version);
        return new[]
        {
            "release", "create", tag, zipPath,
            "--repo", target.Repository,
            "--target", "main",
            "--title", $"Recipes {manifest.Version}",
            "--notes-file", notesPath,
            "--latest=false",
        };
    }

    private static string BuildReleaseNotes(Manifest manifest, PagesTarget target)
    {
        var notes = new StringBuilder();
        notes.AppendLine($"Signed recipes {manifest.Version} for the CP launcher (enabled: {manifest.Enabled.ToString().ToLowerInvariant()}).");
        notes.AppendLine();
        foreach (Pack pack in manifest.Packs)
        {
            notes.AppendLine($"- pack `{pack.Id}`: {pack.Operations.Count} operation(s)");
        }

        string launcher = "none";
        if (manifest.Launcher is not null)
        {
            launcher = manifest.Launcher.Version;
        }

        notes.AppendLine($"- launcher release: {launcher}");
        notes.AppendLine();
        notes.AppendLine($"Deployed to {target.SiteUri} by the `{PagesTarget.Workflow}` workflow, which verifies the archive against the public key the launcher embeds.");
        return notes.ToString();
    }

    private static string FormatArguments(IEnumerable<string> arguments)
    {
        var formatted = new List<string>();
        foreach (string argument in arguments)
        {
            if (argument.Contains(' '))
            {
                formatted.Add($"\"{argument}\"");
            }
            else
            {
                formatted.Add(argument);
            }
        }

        return string.Join(" ", formatted);
    }

    /// <summary>A dist folder as it is, or a published archive extracted into a new scratch folder.</summary>
    private static string OpenDist(string path, List<string> scratch)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            return full;
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"{full} not found.", full);
        }

        string directory = NewScratchPath("l2mod-stage-");
        scratch.Add(directory);
        DistArchive.Extract(full, directory);
        return directory;
    }

    private static string NewScratchPath(string prefix)
    {
        return Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteScratch(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; a locked file only leaves a stray temp folder.
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
                $"# {preview.PackId}: {preview.Caption}, numbered left to right, top to bottom.",
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

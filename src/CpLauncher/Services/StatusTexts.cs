using L2Mod.Core;
using L2Mod.Core.Installation;
using L2Mod.Core.Manifests;
using L2Mod.Core.Updates;

namespace CpLauncher.Services;

public enum StatusSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusText(string Text, StatusSeverity Severity);

/// <summary>
/// Polish texts for Core's installer states and outcomes. Core messages are English and technical, so the UI maps
/// the enums instead and shows Core's text only as a detail.
/// </summary>
public static class StatusTexts
{
    public const string GameRunning = "Gra jest uruchomiona – zamknij ją, zanim launcher zmieni pliki klienta.";

    /// <summary>
    /// Whether a file concerns the member: Icon.utx always; an effect package (system_season) only when the selection
    /// changes it, our patch is in it or it has something pending. An untouched effect package with nothing selected
    /// is not mentioned at all, whatever version it is.
    /// </summary>
    public static bool IsRelevant(PatchFileStatus status)
    {
        if (ClientFiles.IsIconPackage(status.FileName))
        {
            return true;
        }

        return status.OperationCount > 0 || status.State == PatchFileState.OurPatch || status.Pending != PendingAction.None
            || (status.State == PatchFileState.Unknown && status.HasBackup);
    }

    public static StatusText DescribeState(PatchFileStatus status)
    {
        switch (status.State)
        {
            case PatchFileState.Missing:
            {
                return new StatusText($"Nie znaleziono pliku {ClientFiles.GetRelativePath(status.FileName)}.", StatusSeverity.Error);
            }

            case PatchFileState.Original:
            {
                return new StatusText("Oryginalny plik (bez zmian CP).", StatusSeverity.Info);
            }

            case PatchFileState.OurPatch:
            {
                return new StatusText("Zainstalowane zmiany CP.", StatusSeverity.Success);
            }

            default:
            {
                string text = $"Nieznana wersja {status.FileName} – czekam na aktualizację przepisów.";
                if (status.HasBackup)
                {
                    text += " Można przywrócić zapisaną kopię oryginału.";
                }

                return new StatusText(text, StatusSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// The state of all files that concern the member (<see cref="IsRelevant"/>): with Icon.utx alone exactly its
    /// own text; otherwise a missing file, then an unknown version, then "installed" when our patch is in any file.
    /// </summary>
    public static StatusText DescribeState(IReadOnlyList<PatchFileStatus> statuses)
    {
        List<PatchFileStatus> relevant = statuses.Where(IsRelevant).ToList();
        if (relevant.Count == 1)
        {
            return DescribeState(relevant[0]);
        }

        PatchFileStatus? problem = relevant.FirstOrDefault(status => status.State == PatchFileState.Missing)
            ?? relevant.FirstOrDefault(status => status.State == PatchFileState.Unknown);
        if (problem is not null)
        {
            return DescribeState(problem);
        }

        if (relevant.Any(status => status.State == PatchFileState.OurPatch))
        {
            return new StatusText("Zainstalowane zmiany CP.", StatusSeverity.Success);
        }

        return new StatusText("Oryginalne pliki (bez zmian CP).", StatusSeverity.Info);
    }

    /// <summary>
    /// What "Zastosuj" would do for the files that concern the member: what it installs or rebuilds comes first, then
    /// what it restores, then the rest (up to date, waiting for a launcher or recipe update); repeated texts once.
    /// </summary>
    public static string? DescribePending(IReadOnlyList<PatchFileStatus> statuses)
    {
        var texts = new List<string>();
        IEnumerable<PatchFileStatus> ordered = statuses.Where(IsRelevant).OrderBy(status => status.Pending switch
        {
            PendingAction.Install => 0,
            PendingAction.Rebuild => 0,
            PendingAction.Restore => 1,
            _ => 2,
        });
        foreach (PatchFileStatus status in ordered)
        {
            string? text = DescribePending(status);
            if (text is null || texts.Contains(text))
            {
                continue;
            }

            // "Up to date" next to a pending change would contradict it.
            if (status.Pending == PendingAction.None && texts.Count > 0)
            {
                continue;
            }

            texts.Add(text);
        }

        if (texts.Count == 0)
        {
            return null;
        }

        return string.Join(" ", texts);
    }

    /// <summary>What "Zastosuj" would do, from Core's <see cref="PatchReason"/>; null when there is nothing to say.</summary>
    public static string? DescribePending(PatchFileStatus status)
    {
        switch (status.Reason)
        {
            case PatchReason.NotInstalled:
            {
                return "Wybrane paczki nie są jeszcze zainstalowane – kliknij „Zastosuj”.";
            }

            case PatchReason.Outdated:
            {
                return "Przepisy albo wybór paczek się zmieniły – kliknij „Zastosuj”.";
            }

            case PatchReason.SwitchedOff when status.Pending == PendingAction.Restore:
            {
                return $"Lider wyłączył zmiany – oryginalny {status.FileName} zostanie przywrócony.";
            }

            case PatchReason.NothingSelected when status.Pending == PendingAction.Restore:
            {
                if (!ClientFiles.IsIconPackage(status.FileName))
                {
                    return $"„Zastosuj” przywróci oryginalny {status.FileName}, bo nie wybrano już nic, co go zmienia.";
                }

                return "Nie wybrano żadnej paczki ikon – „Zastosuj” przywróci oryginalny Icon.utx.";
            }

            case PatchReason.NeedsNewerLauncher when status.State == PatchFileState.OurPatch:
            {
                return "Zmiany zostają bez zmian do czasu aktualizacji launchera.";
            }

            case PatchReason.UnsupportedOriginal:
            {
                return "Zmiany zbudowano z oryginału, którego przepisy już nie obsługują – przywróć oryginał albo poczekaj na aktualizację przepisów.";
            }

            case PatchReason.UpToDate:
            {
                return "Zmiany są aktualne.";
            }

            default:
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The result of "Zastosuj" for the files that concern the member (<see cref="IsRelevant"/> before it, or changed
    /// or failed by it): with Icon.utx alone exactly its own text; otherwise one sentence per file that changed or
    /// has a problem, or "up to date" when none did. The severity is the worst of them.
    /// </summary>
    /// <param name="before">The status of each file before "Zastosuj" (its operation count and relevance).</param>
    public static StatusText DescribeApply(IReadOnlyList<PatchFileResult> results, IReadOnlyList<PatchFileStatus> before)
    {
        if (results.Any(result => result.Outcome == PatchOutcome.Blocked))
        {
            return new StatusText(GameRunning, StatusSeverity.Warning);
        }

        var described = new List<(PatchFileResult Result, StatusText Text)>();
        foreach (PatchFileResult result in results)
        {
            PatchFileStatus? status = before.FirstOrDefault(item => item.FileName == result.FileName);
            bool changed = result.Outcome is PatchOutcome.Installed or PatchOutcome.Rebuilt or PatchOutcome.Restored or PatchOutcome.Failed;
            if (changed || (status is not null && IsRelevant(status)) || ClientFiles.IsIconPackage(result.FileName))
            {
                described.Add((result, DescribeApply(result, status?.OperationCount ?? 0)));
            }
        }

        if (described.Count == 1)
        {
            return described[0].Text;
        }

        List<StatusText> sentences = described.Where(item => item.Result.Outcome != PatchOutcome.Unchanged).Select(item => item.Text).ToList();
        if (sentences.Count == 0)
        {
            return new StatusText("Bez zmian – pliki są aktualne.", StatusSeverity.Info);
        }

        return Combine(sentences);
    }

    public static StatusText DescribeApply(PatchFileResult result, int operationCount)
    {
        string file = result.FileName;
        bool icons = ClientFiles.IsIconPackage(file);
        string count = $"{operationCount} {Polish.Plural(operationCount, "ikona", "ikony", "ikon")}";
        string where = string.Empty;
        if (!icons)
        {
            count = $"{operationCount} {Polish.Plural(operationCount, "efekt", "efekty", "efektów")}";
            where = $" w {file}";
        }

        switch (result.Outcome)
        {
            case PatchOutcome.Installed:
            {
                return new StatusText($"Zainstalowano zmiany{where} ({count}).", StatusSeverity.Success);
            }

            case PatchOutcome.Rebuilt:
            {
                return new StatusText($"Przebudowano zmiany{where} z oryginału ({count}).", StatusSeverity.Success);
            }

            case PatchOutcome.Restored:
            {
                if (result.Reason == PatchReason.SwitchedOff)
                {
                    return new StatusText($"Lider CP wyłączył zmiany – przywrócono oryginalny {file}.", StatusSeverity.Success);
                }

                return new StatusText($"Przywrócono oryginalny {file}.", StatusSeverity.Success);
            }

            case PatchOutcome.Unchanged:
            {
                return new StatusText($"Bez zmian – {file} jest aktualny.", StatusSeverity.Info);
            }

            case PatchOutcome.Skipped:
            {
                if (result.Reason == PatchReason.FileMissing)
                {
                    return new StatusText($"Pominięto – brak pliku {ClientFiles.GetRelativePath(file)}.", StatusSeverity.Warning);
                }

                return new StatusText($"Pominięto – nieznana wersja {file}. Czekam na aktualizację przepisów.", StatusSeverity.Warning);
            }

            case PatchOutcome.Blocked:
            {
                return new StatusText(GameRunning, StatusSeverity.Warning);
            }

            default:
            {
                if (!icons)
                {
                    return new StatusText($"Nie udało się zastosować zmian w {file}: {result.Message}", StatusSeverity.Error);
                }

                return new StatusText($"Nie udało się zastosować zmian: {result.Message}", StatusSeverity.Error);
            }
        }
    }

    /// <summary>
    /// The result of "Przywróć oryginał": with Icon.utx alone exactly its own text; otherwise the files put back in
    /// one sentence plus any failure. Effect packages without a backup or already original are not mentioned.
    /// </summary>
    public static StatusText DescribeRestore(IReadOnlyList<PatchFileResult> results)
    {
        if (results.Any(result => result.Outcome == PatchOutcome.Blocked))
        {
            return new StatusText(GameRunning, StatusSeverity.Warning);
        }

        List<PatchFileResult> relevant = results
            .Where(result => ClientFiles.IsIconPackage(result.FileName) || result.Outcome is PatchOutcome.Restored or PatchOutcome.Failed)
            .ToList();
        List<PatchFileResult> restored = relevant.Where(result => result.Outcome == PatchOutcome.Restored).ToList();
        if (relevant.Count == 0)
        {
            return DescribeRestore(results[0]);
        }

        if (relevant.Count == 1 || restored.Count == 0)
        {
            PatchFileResult main = relevant.FirstOrDefault(result => result.Outcome == PatchOutcome.Failed) ?? relevant[0];
            return DescribeRestore(main);
        }

        var sentences = new List<StatusText>();
        if (restored.Count == 1)
        {
            sentences.Add(DescribeRestore(restored[0]));
        }
        else
        {
            sentences.Add(new StatusText($"Przywrócono oryginalne pliki: {string.Join(", ", restored.Select(result => result.FileName))}.", StatusSeverity.Success));
        }

        foreach (PatchFileResult failed in relevant.Where(result => result.Outcome == PatchOutcome.Failed))
        {
            sentences.Add(new StatusText($"Nie udało się przywrócić {failed.FileName}: {failed.Message}", StatusSeverity.Error));
        }

        return Combine(sentences);
    }

    public static StatusText DescribeRestore(PatchFileResult result)
    {
        switch (result.Outcome)
        {
            case PatchOutcome.Restored:
            {
                return new StatusText($"Przywrócono oryginalny {result.FileName}.", StatusSeverity.Success);
            }

            case PatchOutcome.Unchanged:
            {
                return new StatusText($"{result.FileName} już jest oryginałem.", StatusSeverity.Info);
            }

            case PatchOutcome.Skipped:
            {
                return new StatusText("Brak kopii oryginału – nie ma czego przywrócić.", StatusSeverity.Warning);
            }

            case PatchOutcome.Blocked:
            {
                return new StatusText(GameRunning, StatusSeverity.Warning);
            }

            default:
            {
                return new StatusText($"Nie udało się przywrócić oryginału: {result.Message}", StatusSeverity.Error);
            }
        }
    }

    public static StatusText DescribePreset(PresetResult result, string presetName)
    {
        switch (result.Outcome)
        {
            case PresetOutcome.Applied:
            {
                int count = result.Changed.Count;
                return new StatusText($"Ustawiono „{presetName}” ({count} {Polish.Plural(count, "opcja", "opcje", "opcji")} w Option.ini).", StatusSeverity.Success);
            }

            case PresetOutcome.Unchanged:
            {
                return new StatusText($"Option.ini już ma ustawienia „{presetName}”.", StatusSeverity.Info);
            }

            case PresetOutcome.Restored:
            {
                return new StatusText("Przywrócono Twoje ustawienia Option.ini.", StatusSeverity.Success);
            }

            case PresetOutcome.NoBackup:
            {
                return new StatusText("Brak kopii Twoich ustawień – nic nie zmieniono.", StatusSeverity.Info);
            }

            case PresetOutcome.Blocked:
            {
                return new StatusText(GameRunning, StatusSeverity.Warning);
            }

            default:
            {
                return new StatusText($"Nie udało się zmienić Option.ini: {result.Message}", StatusSeverity.Error);
            }
        }
    }

    /// <summary>The sentences in one text, with the worst severity.</summary>
    private static StatusText Combine(IReadOnlyList<StatusText> sentences)
    {
        StatusSeverity severity = sentences.Max(sentence => sentence.Severity);
        return new StatusText(string.Join(" ", sentences.Select(sentence => sentence.Text)), severity);
    }

    /// <summary>A Polish line for a manifest note (the parser's English text stays a technical detail).</summary>
    public static string DescribeNote(ManifestNote note)
    {
        string item = "bez nazwy";
        if (note.ItemName is not null)
        {
            item = $"„{note.ItemName}”";
        }
        else if (note.ItemId is not null)
        {
            item = $"„{note.ItemId}”";
        }

        switch (note.Code)
        {
            case ManifestNoteCode.UnsupportedSchema:
            {
                return "Przepisy są w nowszym formacie – paczki i presety poczekają na aktualizację launchera.";
            }

            case ManifestNoteCode.LauncherIgnored:
            {
                return "Nie da się odczytać z przepisów, jaka jest najnowsza wersja launchera.";
            }

            case ManifestNoteCode.OriginalIgnored:
            {
                return "Przepisy wymieniają plik klienta, którego ten launcher nie obsługuje – pominięto go.";
            }

            case ManifestNoteCode.AnnouncementSkipped:
            {
                return $"Pominięto ogłoszenie {item}: {DescribeSkipReason(note.Reason)}.";
            }

            case ManifestNoteCode.AnnouncementLimit:
            {
                return "Pokazano tylko część ogłoszeń (limit launchera).";
            }

            case ManifestNoteCode.PackSkipped:
            {
                return $"Pominięto paczkę {item}: {DescribeSkipReason(note.Reason)}.";
            }

            case ManifestNoteCode.PackLimit:
            {
                return "Przepisy mają więcej paczek, niż obsługuje launcher – pominięto ostatnie.";
            }

            case ManifestNoteCode.PresetSkipped:
            {
                return $"Pominięto preset {item}: {DescribeSkipReason(note.Reason)}.";
            }

            default:
            {
                return "Przepisy mają więcej presetów, niż obsługuje launcher – pominięto ostatnie.";
            }
        }
    }

    /// <summary>The banner after a start by another launcher; null after a normal start.</summary>
    public static StatusText? DescribeRestart(UpdateStartupInfo startup, LauncherVersion current, string? logPath)
    {
        string details = string.Empty;
        if (logPath is not null)
        {
            details = $" Szczegóły: {logPath}.";
        }

        if (startup.FailedVersion is not null)
        {
            return new StatusText($"Nie udało się zainstalować wersji {startup.FailedVersion} launchera – działa dalej wersja {current}. Ta wersja nie będzie już proponowana.{details}", StatusSeverity.Warning);
        }

        if (startup.RolledBackFrom is not null)
        {
            return new StatusText($"Wersja {startup.RolledBackFrom} launchera nie uruchomiła się poprawnie – przywrócono wersję {current}. Wersja {startup.RolledBackFrom} nie będzie już proponowana.{details}", StatusSeverity.Warning);
        }

        if (startup.UpdatedFrom is not null)
        {
            return new StatusText($"Zaktualizowano launcher z wersji {startup.UpdatedFrom} do {current}.", StatusSeverity.Success);
        }

        return null;
    }

    public static string DescribeDownload(DownloadProgress progress)
    {
        string received = Polish.FormatMegabytes(progress.BytesReceived);
        if (progress.TotalBytes is long total && total > 0)
        {
            return $"Pobieranie aktualizacji: {received} z {Polish.FormatMegabytes(total)} MB…";
        }

        return $"Pobieranie aktualizacji: {received} MB…";
    }

    private static string DescribeSkipReason(ManifestSkipReason reason)
    {
        switch (reason)
        {
            case ManifestSkipReason.UnknownOperation:
            case ManifestSkipReason.UnknownField:
            {
                return "wymaga nowszej wersji launchera";
            }

            case ManifestSkipReason.FileNotAllowed:
            {
                return "zmienia plik, którego ten launcher nie obsługuje";
            }

            case ManifestSkipReason.OverLimit:
            {
                return "przekracza limity launchera";
            }

            case ManifestSkipReason.DuplicateId:
            {
                return "ma ten sam identyfikator co inna pozycja";
            }

            case ManifestSkipReason.ForbiddenKey:
            {
                return "zmienia pozycję okien gry, a to niedozwolone";
            }

            default:
            {
                return "błędne dane w przepisach";
            }
        }
    }
}

public static class Polish
{
    /// <summary>1 ikona, 2–4 ikony (but 12–14 ikon), 5+ ikon.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        if (count == 1)
        {
            return one;
        }

        int lastDigit = count % 10;
        int lastTwo = count % 100;
        if (lastDigit >= 2 && lastDigit <= 4 && (lastTwo < 12 || lastTwo > 14))
        {
            return few;
        }

        return many;
    }

    public static string FormatDate(DateOnly date)
    {
        return date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Megabytes with one decimal and a decimal comma: 32,1.</summary>
    public static string FormatMegabytes(long bytes)
    {
        double megabytes = bytes / (1024.0 * 1024.0);
        return megabytes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
    }
}

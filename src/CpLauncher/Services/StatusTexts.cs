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

    public static StatusText DescribeState(PatchFileStatus status)
    {
        switch (status.State)
        {
            case PatchFileState.Missing:
            {
                return new StatusText($"Nie znaleziono pliku {ClientFolder.IconRelativePath}.", StatusSeverity.Error);
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
                string text = "Nieznana wersja Icon.utx – czekam na aktualizację przepisów.";
                if (status.HasBackup)
                {
                    text += " Można przywrócić zapisaną kopię oryginału.";
                }

                return new StatusText(text, StatusSeverity.Warning);
            }
        }
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
                return "Lider wyłączył zmiany – oryginalny Icon.utx zostanie przywrócony.";
            }

            case PatchReason.NothingSelected when status.Pending == PendingAction.Restore:
            {
                return "Nie wybrano żadnej paczki – „Zastosuj” przywróci oryginalny Icon.utx.";
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

    public static StatusText DescribeApply(PatchFileResult result, int operationCount)
    {
        switch (result.Outcome)
        {
            case PatchOutcome.Installed:
            {
                return new StatusText($"Zainstalowano zmiany ({operationCount} {Polish.Plural(operationCount, "ikona", "ikony", "ikon")}).", StatusSeverity.Success);
            }

            case PatchOutcome.Rebuilt:
            {
                return new StatusText($"Przebudowano zmiany z oryginału ({operationCount} {Polish.Plural(operationCount, "ikona", "ikony", "ikon")}).", StatusSeverity.Success);
            }

            case PatchOutcome.Restored:
            {
                if (result.Reason == PatchReason.SwitchedOff)
                {
                    return new StatusText("Lider CP wyłączył zmiany – przywrócono oryginalny Icon.utx.", StatusSeverity.Success);
                }

                return new StatusText("Przywrócono oryginalny Icon.utx.", StatusSeverity.Success);
            }

            case PatchOutcome.Unchanged:
            {
                return new StatusText("Bez zmian – Icon.utx jest aktualny.", StatusSeverity.Info);
            }

            case PatchOutcome.Skipped:
            {
                if (result.Reason == PatchReason.FileMissing)
                {
                    return new StatusText($"Pominięto – brak pliku {ClientFolder.IconRelativePath}.", StatusSeverity.Warning);
                }

                return new StatusText("Pominięto – nieznana wersja Icon.utx. Czekam na aktualizację przepisów.", StatusSeverity.Warning);
            }

            case PatchOutcome.Blocked:
            {
                return new StatusText(GameRunning, StatusSeverity.Warning);
            }

            default:
            {
                return new StatusText($"Nie udało się zastosować zmian: {result.Message}", StatusSeverity.Error);
            }
        }
    }

    public static StatusText DescribeRestore(PatchFileResult result)
    {
        switch (result.Outcome)
        {
            case PatchOutcome.Restored:
            {
                return new StatusText("Przywrócono oryginalny Icon.utx.", StatusSeverity.Success);
            }

            case PatchOutcome.Unchanged:
            {
                return new StatusText("Icon.utx już jest oryginałem.", StatusSeverity.Info);
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

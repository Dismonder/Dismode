using System.ComponentModel;
using System.Globalization;
using GameShift.Core.Profiles;
using Microsoft.UI.Xaml.Media;

namespace GameShift.UI.ViewModels;

public sealed class ProfileListItem : INotifyPropertyChanged
{
    private ImageSource? _posterArtworkSource;
    private ImageSource? _heroArtworkSource;
    private double _dashboardSelectionOpacity;

    public ProfileListItem(
        ManualGameProfile profile,
        GameMetadata? metadata = null)
    {
        Profile = profile;
        Metadata = metadata;
        DisplayName = profile.DisplayName;
        Initial = string.IsNullOrWhiteSpace(DisplayName)
            ? "G"
            : DisplayName.Trim()[..1].ToUpperInvariant();
        ExecutablePath = profile.ExecutablePath;
        PosterArtworkPath = ResolveArtworkPath(profile.ArtworkPath);
        HeroArtworkPath = ResolveArtworkPath(metadata?.HeroArtworkPath);
        ArtworkPath = PosterArtworkPath;
        SourceLabel = string.IsNullOrWhiteSpace(metadata?.Source)
            ? "Profil EXE"
            : metadata.Source;
        LastPlayedLabel = FormatLastPlayed(metadata?.LastPlayedAtUtc);
        PlaytimeLabel = FormatPlaytime(metadata);
        PresetLabel = profile.Preset switch
        {
            OptimizationPreset.Safe => "Bezpieczny",
            OptimizationPreset.Balanced => "Zrównoważony",
            _ => "Nieznany",
        };
        StateLabel = profile.IsEnabled ? "Aktywny" : "Wyłączony";
        HashLabel = $"SHA-256: {profile.ExecutableSha256[..12]}…";
        LaunchAutomationName = $"Uruchom grę {DisplayName}";
        IsLaunchEnabled = profile.IsEnabled;
    }

    public ManualGameProfile Profile { get; }

    public GameMetadata? Metadata { get; }

    public string DisplayName { get; }

    public string Initial { get; }

    public string ExecutablePath { get; }

    public string? ArtworkPath { get; }

    public string? PosterArtworkPath { get; }

    public string? HeroArtworkPath { get; }

    public string SourceLabel { get; }

    public string LastPlayedLabel { get; }

    public string PlaytimeLabel { get; }

    public ImageSource? ArtworkSource
    {
        get => PosterArtworkSource;
        internal set => PosterArtworkSource = value;
    }

    public ImageSource? PosterArtworkSource
    {
        get => _posterArtworkSource;
        internal set
        {
            if (ReferenceEquals(_posterArtworkSource, value))
            {
                return;
            }

            _posterArtworkSource = value;
            PropertyChanged?.Invoke(
                this,
                new(nameof(ArtworkSource)));
            PropertyChanged?.Invoke(
                this,
                new(nameof(PosterArtworkSource)));
            PropertyChanged?.Invoke(
                this,
                new(nameof(HasPosterArtwork)));
            PropertyChanged?.Invoke(
                this,
                new(nameof(HasArtwork)));
            PropertyChanged?.Invoke(
                this,
                new(nameof(FallbackInitialVisibility)));
            PropertyChanged?.Invoke(
                this,
                new(nameof(ArtworkVisibility)));
        }
    }

    public ImageSource? HeroArtworkSource
    {
        get => _heroArtworkSource;
        internal set
        {
            if (ReferenceEquals(_heroArtworkSource, value))
            {
                return;
            }

            _heroArtworkSource = value;
            PropertyChanged?.Invoke(this, new(nameof(HeroArtworkSource)));
            PropertyChanged?.Invoke(this, new(nameof(HasHeroArtwork)));
        }
    }

    public bool HasArtwork => ArtworkSource is not null;

    public bool HasPosterArtwork => PosterArtworkSource is not null;

    public bool HasHeroArtwork => HeroArtworkSource is not null;

    public Microsoft.UI.Xaml.Visibility FallbackInitialVisibility =>
        HasArtwork ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility ArtworkVisibility =>
        HasArtwork ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public double DashboardSelectionOpacity
    {
        get => _dashboardSelectionOpacity;
        internal set
        {
            if (Math.Abs(_dashboardSelectionOpacity - value) < 0.001)
            {
                return;
            }

            _dashboardSelectionOpacity = value;
            PropertyChanged?.Invoke(
                this,
                new(nameof(DashboardSelectionOpacity)));
        }
    }

    public string PresetLabel { get; }

    public string StateLabel { get; }

    public string HashLabel { get; }

    public string LaunchAutomationName { get; }

    public bool IsLaunchEnabled { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() =>
        $"{DisplayName}. {PresetLabel}. {StateLabel}. "
        + $"{ExecutablePath}. {HashLabel}";

    private static string? ResolveArtworkPath(string? artworkPath)
    {
        return File.Exists(artworkPath)
            ? artworkPath
            : null;
    }

    private static string FormatLastPlayed(DateTimeOffset? lastPlayedAtUtc)
    {
        if (lastPlayedAtUtc is null)
        {
            return "Ostatnio grano: brak danych";
        }

        DateTimeOffset local = lastPlayedAtUtc.Value.ToLocalTime();
        DateTimeOffset now = DateTimeOffset.Now;
        int days = Math.Max(0, (now.Date - local.Date).Days);
        string value = days switch
        {
            0 => "dzisiaj",
            1 => "wczoraj",
            <= 30 => $"{days} dni temu",
            _ => local.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
        return $"Ostatnio grano: {value}";
    }

    private static string FormatPlaytime(GameMetadata? metadata)
    {
        if (metadata is null
            || metadata.TotalPlaytimeMinutes <= 0)
        {
            return "Czas gry: brak danych";
        }

        long minutes = metadata.TotalPlaytimeMinutes;
        if (minutes < 60)
        {
            return $"Czas gry: {minutes} min";
        }

        double hours = minutes / 60d;
        return $"Czas gry: {hours.ToString("0.#", CultureInfo.CurrentCulture)} h";
    }
}

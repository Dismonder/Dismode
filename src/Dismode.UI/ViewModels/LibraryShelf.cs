using System.Collections.ObjectModel;

namespace Dismode.UI.ViewModels;

/// <summary>
/// One titled row of the library. Games are grouped so the shelf that needs
/// the player's attention comes first and the rest follow by store, instead
/// of one flat grid where a failed session looks like every other tile.
/// </summary>
public sealed class LibraryShelf
{
    public LibraryShelf(string title, IEnumerable<ProfileListItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);

        Title = title;
        Items = new ObservableCollection<ProfileListItem>(items);
        CountLabel = Items.Count switch
        {
            1 => "1 gra",
            >= 2 and <= 4 => $"{Items.Count} gry",
            _ => $"{Items.Count} gier",
        };
    }

    public string Title { get; }

    public string CountLabel { get; }

    public ObservableCollection<ProfileListItem> Items { get; }
}

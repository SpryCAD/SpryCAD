using Avalonia;
using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.UI;

namespace SpryCad.App;

// a part being opened or imported: a card over its view says so, with the progress (the file read, each feature
// built, the display meshes), until its first rebuild is done
public partial class MainWindow
{
    Border? _loadingCard;
    TextBlock _loadingTitle = null!, _loadingStep = null!;
    ProgressBar _loadingBar = null!;

    /// <summary>Starts the card for a part being opened or imported (<paramref name="reading"/>: its file is read first).</summary>
    void BeginLoading(PartTab t, string what, bool reading = false)
    {
        t.Loading = what;
        t.LoadingRead = reading;
        if (t != _tab) return;
        UpdateLoadingCard();
        if (_loadingCard == null) return;
        _loadingBar.IsIndeterminate = true;
        _loadingStep.Text = reading ? "Reading the file" : "Rebuilding";
    }

    void EndLoading(PartTab t)
    {
        if (t.Loading == null) return;
        t.Loading = null;
        t.LoadingRead = false;
        if (t == _tab) UpdateLoadingCard();
    }

    /// <summary>The card shown for the part shown while it is loading; hidden while the display saved with it shows, or when it could not be opened.</summary>
    void UpdateLoadingCard()
    {
        if (_tab.Loading is not { } what || _tab.Error != null || _tab.Session.SavedDisplay != null)
        {
            if (_loadingCard != null) _loadingCard.IsVisible = false;
            return;
        }
        if (_loadingCard == null)
        {
            _loadingBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 0, IsIndeterminate = true, Margin = new Thickness(0, 6, 0, 6) };
            _loadingStep = Card.Text("", dim: true);
            _loadingCard = Card.Make(CardKind.Info, "", new Control[] { _loadingBar, _loadingStep }, null, out _loadingTitle);
            _loadingCard.IsHitTestVisible = false;
            GraphicsArea.Children.Add(_loadingCard);
        }
        _loadingTitle.Text = what;
        _loadingCard.IsVisible = true;
    }

    void ShowLoadingProgress(PartTab t, RebuildProgress p)
    {
        if (t.Loading == null || p.Preview) return;
        UpdateLoadingCard();
        if (_loadingCard is not { IsVisible: true }) return;
        _loadingBar.IsIndeterminate = p.Meshing || p.Count <= 1;
        _loadingBar.Value = p.Fraction;
        _loadingStep.Text = char.ToUpperInvariant(p.Step[0]) + p.Step[1..];
    }
}

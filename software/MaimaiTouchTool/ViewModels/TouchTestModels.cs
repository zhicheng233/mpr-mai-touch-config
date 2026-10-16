using System.Collections.ObjectModel;
using MaimaiTouchTool.Touch;

namespace MaimaiTouchTool.ViewModels;

public sealed class TouchKeyModel : NotifyModel
{
    private bool isPressed;
    public TouchKeyModel(TouchAsset asset, int index, TouchBoardPosition position) { Asset = asset; Index = index; Position = position; ImagePath = asset.BasePath; }
    public TouchAsset Asset { get; }
    public int Index { get; }
    public TouchBoardPosition Position { get; }
    public string Label => Asset.Label;
    public string ImagePath { get; private set; }
    public bool IsPressed
    {
        get => isPressed;
        set { if (isPressed == value) return; isPressed = value; ImagePath = value ? Asset.PressedPath : Asset.BasePath; OnPropertyChanged(); OnPropertyChanged(nameof(ImagePath)); }
    }
}

public static class TouchTestModelFactory
{
    public static ObservableCollection<TouchKeyModel> Create()
    {
        var positions = TouchManifest.Layout.ToDictionary(position => position.Label, StringComparer.Ordinal);
        return new(TouchManifest.Assets.Select((asset, index) => new TouchKeyModel(asset, index, positions[asset.Label])));
    }
}

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using ShelfRow.App.Services;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.ViewModels;

public class ItemViewModel : INotifyPropertyChanged
{
    private readonly Item _model;
    private readonly ThumbnailStorageManager _thumbnailManager;
    private readonly ThumbnailImageLoader? _imageLoader;
    private ImageSource? _thumbnailImage;
    private bool _isLoadingThumbnail;

    public ItemViewModel(
        Item model,
        ThumbnailStorageManager thumbnailManager,
        ThumbnailImageLoader? imageLoader = null)
    {
        _model = model;
        _thumbnailManager = thumbnailManager;
        _imageLoader = imageLoader;
    }

    public Item Model => _model;
    public Guid Id => _model.Id;
    public string Title => _model.Title;
    public string Author => _model.Author;
    public int Rating => _model.Rating;
    public bool IsUnread => _model.IsUnread;
    public string Genre => _model.Genre;
    public int Pages => _model.Pages;
    public string Memo => _model.Memo;
    public int CoverVersion => _model.CoverVersion;

    public string LocalThumbnailPath => _thumbnailManager.GetLocalThumbnailPath(_model.Id);

    public ImageSource? ThumbnailImage
    {
        get
        {
            if (_thumbnailImage == null && !_isLoadingThumbnail && _imageLoader != null)
            {
                _ = LoadThumbnailAsync();
            }
            return _thumbnailImage;
        }
        private set
        {
            if (_thumbnailImage != value)
            {
                _thumbnailImage = value;
                OnPropertyChanged();
            }
        }
    }

    public async Task LoadThumbnailAsync(string? nasDistributionRoot = null, CancellationToken cancellationToken = default)
    {
        if (_thumbnailImage != null || _isLoadingThumbnail || _imageLoader == null)
            return;

        _isLoadingThumbnail = true;
        try
        {
            var image = await _imageLoader.LoadThumbnailAsync(_model.Id, nasDistributionRoot, cancellationToken);
            if (image != null)
            {
                ThumbnailImage = image;
            }
        }
        finally
        {
            _isLoadingThumbnail = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using ShelfRow.App.Models;
using ShelfRow.App.Services;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.ViewModels;

public class ItemViewModel : INotifyPropertyChanged
{
    private readonly Item _model;
    private readonly ThumbnailStorageManager _thumbnailManager;
    private readonly ThumbnailImageLoader? _imageLoader;
    private readonly Action<Item>? _onModelChanged;
    private readonly CoverGenerationService? _coverGenerator;
    private ImageSource? _thumbnailImage;
    private bool _isLoadingThumbnail;

    public ItemViewModel(
        Item model,
        ThumbnailStorageManager thumbnailManager,
        ThumbnailImageLoader? imageLoader = null,
        Action<Item>? onModelChanged = null,
        CoverGenerationService? coverGenerator = null)
    {
        _model = model;
        _thumbnailManager = thumbnailManager;
        _imageLoader = imageLoader;
        _onModelChanged = onModelChanged;
        _coverGenerator = coverGenerator;
    }

    public Item Model => _model;
    public Guid Id => _model.Id;

    public string Title
    {
        get => _model.Title;
        set
        {
            if (_model.Title != value)
            {
                _model.Title = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public string Author
    {
        get => _model.Author;
        set
        {
            if (_model.Author != value)
            {
                _model.Author = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public int Rating
    {
        get => _model.Rating;
        set
        {
            if (_model.Rating != value)
            {
                _model.Rating = Math.Clamp(value, 0, 5);
                OnPropertyChanged();
                OnPropertyChanged(nameof(RatingStarsText));
                OnPropertyChanged(nameof(RatingValue));
                NotifyChanged();
            }
        }
    }

    /// <summary>
    /// The rating as RatingControl expresses it, where -1 means unrated. Binding a plain
    /// 0 makes the control round it up to one star and write that back through the
    /// binding, so every unrated book picked up a star just by being selected.
    /// </summary>
    public double RatingValue
    {
        get => _model.Rating <= 0 ? -1 : _model.Rating;
        set => Rating = value < 0 ? 0 : (int)value;
    }

    public bool IsUnread
    {
        get => _model.IsUnread;
        set
        {
            if (_model.IsUnread != value)
            {
                _model.IsUnread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UnreadVisibility));
                NotifyChanged();
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility UnreadVisibility =>
        IsUnread ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility AuthorVisibility =>
        !string.IsNullOrWhiteSpace(Author) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility KeywordAVisibility =>
        !string.IsNullOrWhiteSpace(KeywordA) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility KeywordBVisibility =>
        !string.IsNullOrWhiteSpace(KeywordB) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility GenreVisibility =>
        !string.IsNullOrWhiteSpace(Genre) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility RelationVisibility =>
        !string.IsNullOrWhiteSpace(Relation) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string Genre
    {
        get => _model.Genre;
        set
        {
            if (_model.Genre != value)
            {
                _model.Genre = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public string Relation
    {
        get => _model.Relation;
        set
        {
            if (_model.Relation != value)
            {
                _model.Relation = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public string KeywordA
    {
        get => _model.KeywordA;
        set
        {
            if (_model.KeywordA != value)
            {
                _model.KeywordA = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public string KeywordB
    {
        get => _model.KeywordB;
        set
        {
            if (_model.KeywordB != value)
            {
                _model.KeywordB = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public string Memo
    {
        get => _model.Memo;
        set
        {
            if (_model.Memo != value)
            {
                _model.Memo = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }
    }

    public int Pages
    {
        get => _model.Pages;
        set
        {
            if (_model.Pages != value)
            {
                _model.Pages = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PagesText));
                NotifyChanged();
            }
        }
    }

    public int BookType
    {
        get => _model.BookType;
        set
        {
            if (_model.BookType != value)
            {
                _model.BookType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BookTypeGlyph));
                OnPropertyChanged(nameof(BookTypeBrush));
                NotifyChanged();
            }
        }
    }

    public string BookTypeGlyph => BookTypeVisuals.GetGlyph(BookType);
    public SolidColorBrush BookTypeBrush => BookTypeVisuals.GetBrush(BookType);

    public DateTime AddedDate => _model.AddedDate;
    public DateTime? LastReadDate => _model.LastReadDate;

    public string AddedDateText => $"登録日: {_model.AddedDate:yyyy/MM/dd}";
    public string LastReadDateText => _model.LastReadDate.HasValue ? $"読込日: {_model.LastReadDate.Value:yyyy/MM/dd}" : string.Empty;
    public string PagesText => _model.Pages > 0 ? $"ページ数: {_model.Pages}p" : string.Empty;

    public string RatingStarsText
    {
        get
        {
            int r = Math.Clamp(Rating, 0, 5);
            return new string('★', r) + new string('☆', 5 - r);
        }
    }

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
            if (image is null && _coverGenerator is not null
                && await _coverGenerator.GenerateAsync(_model, cancellationToken))
            {
                _imageLoader.InvalidateManifest();
                image = await _imageLoader.LoadThumbnailAsync(_model.Id, nasDistributionRoot, cancellationToken);
            }
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

    public void ReloadThumbnail()
    {
        _thumbnailImage = null;
        _isLoadingThumbnail = false;
        OnPropertyChanged(nameof(ThumbnailImage));
    }

    private void NotifyChanged()
    {
        _onModelChanged?.Invoke(_model);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.ViewModels;

public class ItemViewModel : INotifyPropertyChanged
{
    private readonly Item _model;
    private readonly ThumbnailStorageManager _thumbnailManager;

    public ItemViewModel(Item model, ThumbnailStorageManager thumbnailManager)
    {
        _model = model;
        _thumbnailManager = thumbnailManager;
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

    public string ThumbnailPath => _thumbnailManager.GetLocalThumbnailPath(_model.Id);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.ViewModels;

public sealed class AppCardViewModel : INotifyPropertyChanged
{
    private string _status = "Ready";
    private bool _isBusy;

    public required AppEntry Entry { get; init; }
    public required ImageSource? Icon { get; init; }

    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Category => Entry.Category;
    public bool HasIcon => Icon is not null;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string ActionButtonText => "Install";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(CanRunAction));
        }
    }

    public Brush StatusBrush => Status switch
    {
        "Installed" => Brushes.ForestGreen,
        "Installing" => Brushes.DarkOrange,
        "Requested" => Brushes.SteelBlue,
        "Failed" => Brushes.Firebrick,
        "Installer Missing" => Brushes.Firebrick,
        "Ready" => Brushes.DarkSlateBlue,
        "Running" => Brushes.DarkOrange,
        _ => Brushes.Gray
    };

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRunAction));
        }
    }

    public bool CanRunAction => !IsBusy && Status != "Requested";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

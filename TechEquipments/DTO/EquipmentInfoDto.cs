using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    public sealed class EquipmentInfoDto : INotifyPropertyChanged
    {
        private string _equipName = "";
        private DateTime? _installTime;
        private DateTime? _revisionTime;
        private DateTime? _updatedAt;
        private string? _notes;                 // RTF
        private DateTime? _notesUpdatedAt;

        public string EquipName
        {
            get => _equipName;
            set => SetField(ref _equipName, value);
        }

        public DateTime? InstallTime
        {
            get => _installTime;
            set => SetField(ref _installTime, value);
        }

        public DateTime? RevisionTime
        {
            get => _revisionTime;
            set => SetField(ref _revisionTime, value);
        }

        public DateTime? UpdatedAt
        {
            get => _updatedAt;
            set => SetField(ref _updatedAt, value);
        }

        /// <summary> Notes в формате RTF для RichEditControl.</summary>
        public string? Notes
        {
            get => _notes;
            set => SetField(ref _notes, value);
        }

        public DateTime? NotesUpdatedAt
        {
            get => _notesUpdatedAt;
            set
            {
                if (Equals(_notesUpdatedAt, value))
                    return;

                _notesUpdatedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NotesUpdatedAtText));
            }
        }

        public string NotesUpdatedAtText =>
            NotesUpdatedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";

        public ObservableCollection<EquipmentInfoFileDto> Photos { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> Instructions { get; } = new();
        public ObservableCollection<EquipmentInfoFileDto> Schemes { get; } = new();

        public static EquipmentInfoDto CreateEmpty(string equipName)
        {
            return new EquipmentInfoDto
            {
                EquipName = (equipName ?? "").Trim()
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
                return;

            field = value;
            OnPropertyChanged(propertyName);
        }
    }
}
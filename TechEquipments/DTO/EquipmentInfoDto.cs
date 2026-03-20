using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Карточка Info по одному оборудованию.
    /// Храним одну картинку и один PDF.
    /// </summary>
    public sealed class EquipmentInfoDto : INotifyPropertyChanged
    {
        private string _equipName = "";
        private DateTime? _installTime;
        private DateTime? _revisionTime;
        private byte[]? _imageData;
        private string? _imageFileName;
        private byte[]? _pdfData;
        private string? _pdfFileName;
        private DateTime? _updatedAt;

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

        public byte[]? ImageData
        {
            get => _imageData;
            set => SetField(ref _imageData, value);
        }

        public string? ImageFileName
        {
            get => _imageFileName;
            set => SetField(ref _imageFileName, value);
        }

        public byte[]? PdfData
        {
            get => _pdfData;
            set => SetField(ref _pdfData, value);
        }

        public string? PdfFileName
        {
            get => _pdfFileName;
            set => SetField(ref _pdfFileName, value);
        }

        public DateTime? UpdatedAt
        {
            get => _updatedAt;
            set => SetField(ref _updatedAt, value);
        }

        public static EquipmentInfoDto CreateEmpty(string equipName) => new()
        {
            EquipName = (equipName ?? "").Trim()
        };

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
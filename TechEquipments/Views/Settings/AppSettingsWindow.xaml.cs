using DevExpress.Xpf.Core;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text;
using System.Windows;

namespace TechEquipments.Views.Settings
{
    /// <summary>
    /// Модальное окно редактирования appsettings.json.
    ///
    /// Важно:
    /// - редактируем runtime-файл рядом с exe;
    /// - сохраняем как есть (включая комментарии, если они есть);
    /// - перед сохранением делаем базовую проверку через ConfigurationBuilder,
    ///   чтобы не записать совсем сломанный JSON/appsettings.
    /// </summary>
    public partial class AppSettingsWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private readonly string _settingsPath;

        public AppSettingsWindow(string settingsPath)
        {
            InitializeComponent();

            _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
            PathTextBlock.Text = $"File: {_settingsPath}";

            LoadSettingsText();
        }

        /// <summary>
        /// Загружает текст appsettings.json в редактор.
        /// Если файла ещё нет — просто показываем пустой редактор.
        /// </summary>
        private void LoadSettingsText()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    EditorTextBox.Text = File.ReadAllText(_settingsPath, Encoding.UTF8);
                }
                else
                {
                    EditorTextBox.Text = "";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to read settings file.\n\n{ex.Message}",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Apply:
        /// 1) валидируем содержимое;
        /// 2) сохраняем в runtime appsettings.json;
        /// 3) показываем сообщение о необходимости перезапуска;
        /// 4) закрываем окно.
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var text = EditorTextBox.Text ?? string.Empty;

            try
            {
                ValidateSettingsText(text);

                var dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_settingsPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                DXMessageBox.Show(
                    "Settings were saved successfully.\nPlease restart the application for the changes to take effect.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(
                    $"Failed to save settings.\n\n{ex.Message}",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Проверяет, что текст можно прочитать как appsettings-конфиг.
        /// Это мягкая защита от сохранения битого файла.
        /// </summary>
        private static void ValidateSettingsText(string text)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

            var cfg = new ConfigurationBuilder()
                .AddJsonStream(ms)
                .Build();

            // Просто факт Build() уже означает, что формат читается.
            _ = cfg;
        }

        /// <summary>
        /// Отмена без сохранения.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
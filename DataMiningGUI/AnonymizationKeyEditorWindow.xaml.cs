using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace DataMiningGUI
{
    public partial class AnonymizationKeyEditorWindow : Window
    {
        private string _keyFilePath;
        private ObservableCollection<MappingItem> _mappings;
        private bool _hasChanges = false;
        public string SelectedKeyFilePath => _keyFilePath;

        public AnonymizationKeyEditorWindow(string defaultFolder)
        {
            InitializeComponent();
            _mappings = new ObservableCollection<MappingItem>();
            MappingsDataGrid.ItemsSource = _mappings;

            // Set up change tracking
            _mappings.CollectionChanged += (s, e) => _hasChanges = true;

            // Set default path
            SetKeyFilePath(defaultFolder);
            LoadMappings();
        }

        private void SetKeyFilePath(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                _keyFilePath = null;
                KeyFilePathTextBox.Text = "";
                FileStatusTextBlock.Text = "No folder selected";
                return;
            }

            _keyFilePath = Path.Combine(folder, "AnonymizationKey.json");
            KeyFilePathTextBox.Text = _keyFilePath;

            if (File.Exists(_keyFilePath))
            {
                FileStatusTextBlock.Text = $"File exists • {_mappings.Count} mappings loaded";
            }
            else
            {
                FileStatusTextBlock.Text = "File does not exist (will be created on save)";
            }
        }

        private void LoadMappings()
        {
            _mappings.Clear();

            if (string.IsNullOrEmpty(_keyFilePath) || !File.Exists(_keyFilePath))
            {
                UpdateFileStatus();
                return;
            }

            try
            {
                string json = File.ReadAllText(_keyFilePath);
                AnonymizationKey anonKey = JsonConvert.DeserializeObject<AnonymizationKey>(json);

                if (anonKey != null && anonKey.Mappings != null)
                {
                    foreach (var kvp in anonKey.Mappings)
                    {
                        _mappings.Add(new MappingItem
                        {
                            OriginalMRN = kvp.Key,
                            AnonymizedID = kvp.Value
                        });
                    }
                }

                _hasChanges = false;
                UpdateFileStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading anonymization key: {ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateFileStatus()
        {
            if (File.Exists(_keyFilePath))
            {
                FileStatusTextBlock.Text = $"File exists • {_mappings.Count} mappings loaded";
            }
            else
            {
                FileStatusTextBlock.Text = "File does not exist (will be created on save)";
            }
        }

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select folder for AnonymizationKey.json";
                dialog.ShowNewFolderButton = true;

                if (!string.IsNullOrEmpty(_keyFilePath))
                {
                    string currentFolder = Path.GetDirectoryName(_keyFilePath);
                    if (Directory.Exists(currentFolder))
                    {
                        dialog.SelectedPath = currentFolder;
                    }
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (_hasChanges)
                    {
                        var result = MessageBox.Show(
                            "You have unsaved changes. Do you want to save them before changing folders?",
                            "Unsaved Changes",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            SaveMappings();
                        }
                        else if (result == MessageBoxResult.Cancel)
                        {
                            return;
                        }
                    }

                    SetKeyFilePath(dialog.SelectedPath);
                    LoadMappings();
                }
            }
        }

        private void AddMappingButton_Click(object sender, RoutedEventArgs e)
        {
            var newMapping = new MappingItem
            {
                OriginalMRN = "NEW_MRN",
                AnonymizedID = "NEW_HASH"
            };
            _mappings.Add(newMapping);
            _hasChanges = true;

            // Select the new row for editing
            MappingsDataGrid.SelectedItem = newMapping;
            MappingsDataGrid.ScrollIntoView(newMapping);
            MappingsDataGrid.Focus();
        }

        private void DeleteMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (MappingsDataGrid.SelectedItem is MappingItem selectedMapping)
            {
                var result = MessageBox.Show(
                    $"Delete mapping for '{selectedMapping.OriginalMRN}'?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _mappings.Remove(selectedMapping);
                    _hasChanges = true;
                    UpdateFileStatus();
                }
            }
            else
            {
                MessageBox.Show("Please select a mapping to delete.",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveMappings())
            {
                this.DialogResult = true;
                this.Close();
            }
        }

        private bool SaveMappings()
        {
            if (string.IsNullOrEmpty(_keyFilePath))
            {
                MessageBox.Show("Please select a folder first.",
                    "No Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                // Validate mappings
                var seenOriginal = new System.Collections.Generic.HashSet<string>();
                var seenAnonymized = new System.Collections.Generic.HashSet<string>();

                foreach (var mapping in _mappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.OriginalMRN))
                    {
                        MessageBox.Show("All mappings must have an Original MRN.",
                            "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(mapping.AnonymizedID))
                    {
                        MessageBox.Show("All mappings must have an Anonymized ID.",
                            "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    if (seenOriginal.Contains(mapping.OriginalMRN))
                    {
                        MessageBox.Show($"Duplicate Original MRN: {mapping.OriginalMRN}",
                            "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }

                    seenOriginal.Add(mapping.OriginalMRN);
                    seenAnonymized.Add(mapping.AnonymizedID);
                }

                // Create AnonymizationKey object
                var anonKey = new AnonymizationKey();
                foreach (var mapping in _mappings)
                {
                    anonKey.Mappings[mapping.OriginalMRN] = mapping.AnonymizedID;
                }

                // Ensure directory exists
                string directory = Path.GetDirectoryName(_keyFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Save to file
                string json = anonKey.ToJsonFormatted();
                File.WriteAllText(_keyFilePath, json);

                _hasChanges = false;
                MessageBox.Show("Anonymization key saved successfully.",
                    "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateFileStatus();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving anonymization key: {ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Discard them?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            this.DialogResult = false;
            this.Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_hasChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Discard them?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                }
            }

            base.OnClosing(e);
        }
    }

    public class MappingItem : INotifyPropertyChanged
    {
        private string _originalMRN;
        private string _anonymizedID;

        public string OriginalMRN
        {
            get => _originalMRN;
            set
            {
                if (_originalMRN != value)
                {
                    _originalMRN = value;
                    OnPropertyChanged(nameof(OriginalMRN));
                }
            }
        }

        public string AnonymizedID
        {
            get => _anonymizedID;
            set
            {
                if (_anonymizedID != value)
                {
                    _anonymizedID = value;
                    OnPropertyChanged(nameof(AnonymizedID));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
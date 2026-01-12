using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DataMiningGUI
{
    public partial class ExportWindow : Window
    {
        #region Fields

        private readonly List<PatientDisplayItem> _dataToExport;
        private readonly Dictionary<string, CheckBox> _fieldCheckBoxes;
        private readonly Dictionary<string, ExportFieldInfo> _exportFields;

        #endregion

        #region Constructor

        public ExportWindow(List<PatientDisplayItem> dataToExport)
        {
            InitializeComponent();

            _dataToExport = dataToExport ?? new List<PatientDisplayItem>();
            _fieldCheckBoxes = new Dictionary<string, CheckBox>();
            _exportFields = new Dictionary<string, ExportFieldInfo>();

            InitializeExportFields();
            CreateFieldCheckBoxes();
            UpdateRecordCount();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Defines the available export fields with display names and property accessors
        /// </summary>
        private void InitializeExportFields()
        {
            _exportFields.Add("MRN", new ExportFieldInfo
            {
                DisplayName = "MRN",
                GetValue = item => item.MRN
            });

            _exportFields.Add("PatientName", new ExportFieldInfo
            {
                DisplayName = "Patient Name",
                GetValue = item => item.PatientName
            });

            _exportFields.Add("CourseName", new ExportFieldInfo
            {
                DisplayName = "Course",
                GetValue = item => item.CourseName
            });

            _exportFields.Add("PlanName", new ExportFieldInfo
            {
                DisplayName = "Plan Name",
                GetValue = item => item.PlanName
            });

            _exportFields.Add("PlanType", new ExportFieldInfo
            {
                DisplayName = "Plan Type",
                GetValue = item => item.PlanType
            });

            _exportFields.Add("MachineName", new ExportFieldInfo
            {
                DisplayName = "Machine Name",
                GetValue = item => item.MachineName
            });

            _exportFields.Add("NumberOfFractions", new ExportFieldInfo
            {
                DisplayName = "Number of Fractions",
                GetValue = item => item.NumberOfFractions
            });

            _exportFields.Add("DosePerFraction", new ExportFieldInfo
            {
                DisplayName = "Dose per Fraction (cGy)",
                GetValue = item => item.DosePerFraction
            });

            _exportFields.Add("TotalDose", new ExportFieldInfo
            {
                DisplayName = "Total Dose (cGy)",
                GetValue = item => item.TotalDose
            });

            _exportFields.Add("Energy", new ExportFieldInfo
            {
                DisplayName = "Energy",
                GetValue = item => item.Energy
            });

            _exportFields.Add("Technique", new ExportFieldInfo
            {
                DisplayName = "Technique",
                GetValue = item => item.Technique
            });

            _exportFields.Add("PlannedBy", new ExportFieldInfo
            {
                DisplayName = "Planned By",
                GetValue = item => item.PlannedBy
            });

            _exportFields.Add("ApprovalStatus", new ExportFieldInfo
            {
                DisplayName = "Approval Status",
                GetValue = item => item.ApprovalStatus
            });

            _exportFields.Add("ReviewDateTime", new ExportFieldInfo
            {
                DisplayName = "Review Date",
                GetValue = item => item.ReviewDateTime
            });
        }

        /// <summary>
        /// Creates checkboxes for all export fields
        /// </summary>
        private void CreateFieldCheckBoxes()
        {
            // Create checkboxes in the defined order
            var fieldKeys = new[]
            {
                "MRN", "PatientName", "CourseName", "PlanName", "PlanType",
                "MachineName", "NumberOfFractions", "DosePerFraction", "TotalDose",
                "Energy", "Technique", "PlannedBy", "ApprovalStatus", "ReviewDateTime"
            };

            foreach (var key in fieldKeys)
            {
                if (_exportFields.TryGetValue(key, out var fieldInfo))
                {
                    var checkBox = new CheckBox
                    {
                        Content = fieldInfo.DisplayName,
                        IsChecked = true, // Default to checked
                        Tag = key,
                        Style = (Style)FindResource("FieldCheckBoxStyle")
                    };

                    _fieldCheckBoxes[key] = checkBox;
                    FieldCheckBoxPanel.Children.Add(checkBox);
                }
            }
        }

        /// <summary>
        /// Updates the record count display
        /// </summary>
        private void UpdateRecordCount()
        {
            RecordCountText.Text = $"{_dataToExport.Count} record(s) to export";
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Selects all fields
        /// </summary>
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkBox in _fieldCheckBoxes.Values)
            {
                checkBox.IsChecked = true;
            }
        }

        /// <summary>
        /// Deselects all fields
        /// </summary>
        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkBox in _fieldCheckBoxes.Values)
            {
                checkBox.IsChecked = false;
            }
        }

        /// <summary>
        /// Handles the Export button click
        /// </summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate selection
                var selectedFieldKeys = _fieldCheckBoxes
                    .Where(kvp => kvp.Value.IsChecked == true)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (selectedFieldKeys.Count == 0)
                {
                    MessageBox.Show("Please select at least one field to export.",
                        "No Fields Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_dataToExport.Count == 0)
                {
                    MessageBox.Show("No data to export.",
                        "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Show save file dialog
                var saveFileDialog = new SaveFileDialog
                {
                    Title = "Export Patient Data",
                    Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                    DefaultExt = ".csv",
                    FileName = $"PatientData_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (saveFileDialog.ShowDialog() != true)
                    return;

                // Export to CSV
                ExportToCsv(saveFileDialog.FileName, selectedFieldKeys);

                // Show success message
                var result = MessageBox.Show(
                    $"Successfully exported {_dataToExport.Count} record(s) to:\n{saveFileDialog.FileName}\n\nWould you like to open the file?",
                    "Export Successful",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(saveFileDialog.FileName);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting data:\n{ex.Message}",
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion

        #region CSV Export

        /// <summary>
        /// Exports the data to a CSV file
        /// </summary>
        private void ExportToCsv(string filePath, List<string> selectedFieldKeys)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                // Write header row
                var headers = selectedFieldKeys
                    .Select(key => _exportFields[key].DisplayName)
                    .ToList();

                writer.WriteLine(string.Join(",", headers.Select(EscapeCsvValue)));

                // Write data rows
                foreach (var item in _dataToExport)
                {
                    var values = selectedFieldKeys
                        .Select(key => _exportFields[key].GetValue(item) ?? "")
                        .ToList();

                    writer.WriteLine(string.Join(",", values.Select(EscapeCsvValue)));
                }
            }
        }

        /// <summary>
        /// Escapes a value for CSV format (handles commas, quotes, and newlines)
        /// </summary>
        private string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            // If the value contains comma, quote, or newline, wrap it in quotes and escape internal quotes
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        #endregion
    }

    #region Helper Classes

    /// <summary>
    /// Information about an exportable field
    /// </summary>
    public class ExportFieldInfo
    {
        public string DisplayName { get; set; }
        public Func<PatientDisplayItem, string> GetValue { get; set; }
    }

    #endregion
}

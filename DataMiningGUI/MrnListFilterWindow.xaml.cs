using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DataMiningGUI
{
    /// <summary>
    /// Interaction logic for MrnListFilterWindow.xaml
    /// Allows the user to paste or load a list of MRNs to use as a whitelist filter.
    /// </summary>
    public partial class MrnListFilterWindow : Window
    {
        /// <summary>
        /// The resulting set of MRNs after Apply is clicked.
        /// Null means "no filter" (Remove Filter was clicked).
        /// Empty set is treated the same as null by the caller.
        /// </summary>
        public HashSet<string> ResultMrnSet { get; private set; }

        public MrnListFilterWindow(HashSet<string> existingWhitelist)
        {
            InitializeComponent();

            // Pre-populate the text box if there's an existing whitelist
            if (existingWhitelist != null && existingWhitelist.Count > 0)
            {
                MrnTextBox.Text = string.Join(Environment.NewLine, existingWhitelist.OrderBy(m => m));
                UpdateMrnCount();
            }
        }

        #region Event Handlers

        /// <summary>
        /// Pastes clipboard text into the MRN text box, appending to existing content
        /// </summary>
        private void PasteFromClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clipboardText = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(clipboardText))
                    {
                        if (!string.IsNullOrWhiteSpace(MrnTextBox.Text))
                        {
                            MrnTextBox.Text += Environment.NewLine;
                        }
                        MrnTextBox.Text += clipboardText;
                        MrnTextBox.ScrollToEnd();
                    }
                }
                else
                {
                    StatusText.Text = "Clipboard does not contain text.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error reading clipboard: {ex.Message}";
            }
        }

        /// <summary>
        /// Loads MRNs from a text or CSV file
        /// </summary>
        private void LoadFromFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Title = "Load MRN List",
                    Filter = "Text Files (*.txt)|*.txt|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                    DefaultExt = ".txt"
                };

                if (openDialog.ShowDialog() == true)
                {
                    string fileContent = File.ReadAllText(openDialog.FileName);
                    if (!string.IsNullOrWhiteSpace(MrnTextBox.Text))
                    {
                        MrnTextBox.Text += Environment.NewLine;
                    }
                    MrnTextBox.Text += fileContent;
                    MrnTextBox.ScrollToEnd();
                    StatusText.Text = $"Loaded MRNs from {Path.GetFileName(openDialog.FileName)}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading file: {ex.Message}";
            }
        }

        /// <summary>
        /// Clears the text box
        /// </summary>
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            MrnTextBox.Clear();
        }

        /// <summary>
        /// Updates the MRN count as the user types or pastes
        /// </summary>
        private void MrnTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMrnCount();
        }

        /// <summary>
        /// Applies the MRN list as a whitelist filter
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ResultMrnSet = ParseMrns(MrnTextBox.Text);

            if (ResultMrnSet.Count == 0)
            {
                var result = MessageBox.Show(
                    "No valid MRNs were found in the text. Do you want to remove the MRN filter instead?",
                    "No MRNs Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    ResultMrnSet = null;
                    DialogResult = true;
                    Close();
                }
                return;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Removes the MRN whitelist filter entirely
        /// </summary>
        private void RemoveFilter_Click(object sender, RoutedEventArgs e)
        {
            ResultMrnSet = null;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Cancels without changing the current filter state
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Parses the text content into a set of unique, trimmed, non-empty MRN strings.
        /// Handles newlines, commas, tabs, and semicolons as delimiters.
        /// </summary>
        private static HashSet<string> ParseMrns(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var separators = new[] { '\r', '\n', ',', '\t', ';' };
            var mrns = text
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return mrns;
        }

        /// <summary>
        /// Updates the MRN count display
        /// </summary>
        private void UpdateMrnCount()
        {
            var mrns = ParseMrns(MrnTextBox.Text);
            MrnCountText.Text = $"{mrns.Count} unique MRN(s)";
        }

        #endregion
    }
}
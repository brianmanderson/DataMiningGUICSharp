using DataBaseStructure.AriaBase;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

namespace DataMiningGUI
{
    /// <summary>
    /// Template selector for filter items (criteria vs groups)
    /// </summary>
    public class FilterItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate CriterionTemplate { get; set; }
        public DataTemplate GroupTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is FilterGroup)
                return GroupTemplate;
            if (item is FilterCriterion)
                return CriterionTemplate;

            return base.SelectTemplate(item, container);
        }
    }

    /// <summary>
    /// Interaction logic for FilterWindow.xaml
    /// </summary>
    public partial class FilterWindow : Window
    {
        #region Properties

        /// <summary>
        /// The filter configuration being edited
        /// </summary>
        public FilterConfiguration FilterConfig { get; private set; }

        /// <summary>
        /// Whether the user applied the filter (vs cancelled)
        /// </summary>
        public bool FilterApplied { get; private set; }

        /// <summary>
        /// Reference to patients for preview functionality
        /// </summary>
        private readonly List<PatientClass> _patients;

        /// <summary>
        /// Observable collection of filter items for UI binding
        /// </summary>
        public ObservableCollection<FilterItemBase> FilterItems { get; private set; }

        /// <summary>
        /// Whether this window is editing a group (vs main filter)
        /// </summary>
        private readonly bool _isGroupEditor;

        /// <summary>
        /// The group being edited (if in group edit mode)
        /// </summary>
        private readonly FilterGroup _editingGroup;

        // Enum items for combo box binding
        public List<EnumItem<FilterField>> FieldItems { get; private set; }
        public List<EnumItem<FilterOperator>> OperatorItems { get; private set; }
        public List<EnumItem<LogicalOperator>> LogicalOperatorItems { get; private set; }

        // Default filter directory
        private static string _lastFilterDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new FilterWindow for main filter editing
        /// </summary>
        /// <param name="currentConfig">The current filter configuration (can be null)</param>
        /// <param name="patients">List of patients for preview functionality</param>
        public FilterWindow(FilterConfiguration currentConfig, List<PatientClass> patients)
            : this(currentConfig, patients, false, null)
        {
        }

        /// <summary>
        /// Creates a new FilterWindow (main filter or group editor)
        /// </summary>
        private FilterWindow(FilterConfiguration currentConfig, List<PatientClass> patients,
            bool isGroupEditor, FilterGroup editingGroup)
        {
            InitializeComponent();

            _patients = patients ?? new List<PatientClass>();
            _isGroupEditor = isGroupEditor;
            _editingGroup = editingGroup;

            // Initialize enum items for combo boxes
            FieldItems = EnumHelper.GetEnumItems<FilterField>();
            OperatorItems = EnumHelper.GetEnumItems<FilterOperator>();
            LogicalOperatorItems = EnumHelper.GetEnumItems<LogicalOperator>();

            // Initialize items collection
            FilterItems = new ObservableCollection<FilterItemBase>();

            if (_isGroupEditor && _editingGroup != null)
            {
                // Editing a group - copy its items
                Title = $"Edit Group: {_editingGroup.GroupName}";
                foreach (var item in _editingGroup.Items)
                {
                    FilterItems.Add(item.Clone());
                }
            }
            else if (currentConfig != null && currentConfig.Items.Any())
            {
                // Editing main filter - copy existing items
                foreach (var item in currentConfig.Items)
                {
                    FilterItems.Add(item.Clone());
                }
            }

            // Bind to UI
            ItemsListControl.ItemsSource = FilterItems;

            // Update UI state
            UpdateEmptyState();
            UpdateFilterSummary();

            DataContext = this;
        }

        /// <summary>
        /// Creates a FilterWindow for editing a group
        /// </summary>
        public static FilterWindow CreateGroupEditor(FilterGroup group, List<PatientClass> patients, Window owner)
        {
            var window = new FilterWindow(null, patients, true, group)
            {
                Owner = owner,
                Title = $"Edit Group: {group.GroupName}"
            };
            return window;
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Adds a new filter criterion
        /// </summary>
        private void AddCriterion_Click(object sender, RoutedEventArgs e)
        {
            var criterion = new FilterCriterion
            {
                Field = FilterField.PlanName,
                Operator = FilterOperator.Contains,
                Value = "",
                LogicalOperator = LogicalOperator.And,
                IsEnabled = true
            };

            FilterItems.Add(criterion);
            UpdateEmptyState();
            UpdateFilterSummary();
        }

        /// <summary>
        /// Adds a new filter group
        /// </summary>
        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = new FilterGroup
            {
                GroupName = $"Group {FilterItems.Count(i => i is FilterGroup) + 1}",
                LogicalOperator = LogicalOperator.And,
                IsEnabled = true
            };

            // Add a default criterion to the group
            group.Items.Add(new FilterCriterion
            {
                Field = FilterField.PlanName,
                Operator = FilterOperator.Contains,
                Value = "",
                LogicalOperator = LogicalOperator.Or,
                IsEnabled = true
            });

            FilterItems.Add(group);
            UpdateEmptyState();
            UpdateFilterSummary();

            // Open the group editor immediately
            EditGroup(group);
        }

        /// <summary>
        /// Edits a filter group
        /// </summary>
        private void EditGroup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FilterGroup group)
            {
                EditGroup(group);
            }
        }

        /// <summary>
        /// Opens the group editor dialog
        /// </summary>
        private void EditGroup(FilterGroup group)
        {
            var groupEditor = CreateGroupEditor(group, _patients, this);

            if (groupEditor.ShowDialog() == true && groupEditor.FilterApplied)
            {
                // Update the group with the edited items
                group.Items.Clear();
                foreach (var item in groupEditor.FilterItems)
                {
                    group.Items.Add(item);
                }

                // Refresh the display
                var index = FilterItems.IndexOf(group);
                if (index >= 0)
                {
                    FilterItems.RemoveAt(index);
                    FilterItems.Insert(index, group);
                }

                UpdateFilterSummary();
            }
        }

        /// <summary>
        /// Removes a filter item (criterion or group)
        /// </summary>
        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FilterItemBase item)
            {
                FilterItems.Remove(item);
                UpdateEmptyState();
                UpdateFilterSummary();
            }
        }

        /// <summary>
        /// Clears all filter items
        /// </summary>
        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (FilterItems.Any())
            {
                var result = MessageBox.Show(
                    "Are you sure you want to clear all filter criteria?",
                    "Confirm Clear",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    FilterItems.Clear();
                    UpdateEmptyState();
                    UpdateFilterSummary();
                    PreviewCountText.Text = "0 matches";
                }
            }
        }

        /// <summary>
        /// Saves the current filter to a JSON file
        /// </summary>
        private void SaveFilter_Click(object sender, RoutedEventArgs e)
        {
            if (!FilterItems.Any())
            {
                MessageBox.Show("No filter criteria to save.", "Save Filter",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Title = "Save Filter Configuration",
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".json",
                    InitialDirectory = _lastFilterDirectory,
                    FileName = "PatientFilter"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    _lastFilterDirectory = Path.GetDirectoryName(saveDialog.FileName);

                    var config = BuildFilterConfiguration();
                    var json = FilterSerializer.SerializeFilter(config);
                    File.WriteAllText(saveDialog.FileName, json);

                    MessageBox.Show($"Filter saved successfully to:\n{saveDialog.FileName}",
                        "Save Filter", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving filter:\n{ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads a filter from a JSON file
        /// </summary>
        private void LoadFilter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Title = "Load Filter Configuration",
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".json",
                    InitialDirectory = _lastFilterDirectory
                };

                if (openDialog.ShowDialog() == true)
                {
                    _lastFilterDirectory = Path.GetDirectoryName(openDialog.FileName);

                    var json = File.ReadAllText(openDialog.FileName);
                    var config = FilterSerializer.DeserializeFilter(json);

                    if (config == null || !config.Items.Any())
                    {
                        MessageBox.Show("The selected file does not contain valid filter criteria.",
                            "Load Filter", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Ask user if they want to replace or merge
                    MessageBoxResult mergeResult = MessageBoxResult.Yes;
                    if (FilterItems.Any())
                    {
                        mergeResult = MessageBox.Show(
                            "Do you want to replace the current filter?\n\n" +
                            "Yes = Replace current filter\n" +
                            "No = Add to current filter\n" +
                            "Cancel = Don't load",
                            "Load Filter",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        if (mergeResult == MessageBoxResult.Cancel)
                            return;

                        if (mergeResult == MessageBoxResult.Yes)
                            FilterItems.Clear();
                    }

                    // Add loaded items
                    foreach (var item in config.Items)
                    {
                        FilterItems.Add(item);
                    }

                    UpdateEmptyState();
                    UpdateFilterSummary();

                    MessageBox.Show($"Filter loaded successfully from:\n{openDialog.FileName}",
                        "Load Filter", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading filter:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Previews the filter results
        /// </summary>
        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tempConfig = BuildFilterConfiguration();
                tempConfig.IsActive = true;

                var results = PatientFilterEngine.FilterPatients(tempConfig, _patients);
                PreviewCountText.Text = $"{results.Count} matches";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error previewing filter: {ex.Message}",
                    "Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Applies the filter and closes the window
        /// </summary>
        private void ApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_isGroupEditor)
            {
                // For group editor, just set the result and close
                FilterApplied = true;
                DialogResult = true;
                Close();
            }
            else
            {
                FilterConfig = BuildFilterConfiguration();
                FilterConfig.IsActive = FilterItems.Any(i => i.IsEnabled);
                FilterApplied = true;
                DialogResult = true;
                Close();
            }
        }

        /// <summary>
        /// Cancels and closes the window
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            FilterApplied = false;
            DialogResult = false;
            Close();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Builds a FilterConfiguration from the current items
        /// </summary>
        private FilterConfiguration BuildFilterConfiguration()
        {
            var config = new FilterConfiguration();

            foreach (var item in FilterItems)
            {
                config.Items.Add(item.Clone());
            }

            return config;
        }

        /// <summary>
        /// Updates the empty state text visibility
        /// </summary>
        private void UpdateEmptyState()
        {
            EmptyStateText.Visibility = FilterItems.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Updates the filter summary text
        /// </summary>
        private void UpdateFilterSummary()
        {
            if (!FilterItems.Any())
            {
                FilterSummaryText.Text = "No filter criteria defined";
                return;
            }

            var enabledCount = FilterItems.Count(i => i.IsEnabled);
            var groupCount = FilterItems.Count(i => i is FilterGroup && i.IsEnabled);
            var criterionCount = FilterItems.Count(i => i is FilterCriterion && i.IsEnabled);

            if (enabledCount == 0)
            {
                FilterSummaryText.Text = "All items disabled";
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"{enabledCount} item(s) active");

            if (groupCount > 0)
                sb.Append($" ({groupCount} group(s), {criterionCount} criterion/criteria)");

            sb.Append(": ");

            var summaryParts = new List<string>();
            foreach (var item in FilterItems.Where(i => i.IsEnabled).Take(2))
            {
                summaryParts.Add(item.DisplaySummary);
            }

            sb.Append(string.Join(" -> ", summaryParts));

            if (enabledCount > 2)
            {
                sb.Append($" (+{enabledCount - 2} more)");
            }

            FilterSummaryText.Text = sb.ToString();
        }

        #endregion
    }

    #region Filter Serialization

    /// <summary>
    /// Handles JSON serialization/deserialization of filter configurations
    /// </summary>
    public static class FilterSerializer
    {
        private static readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            Formatting = Newtonsoft.Json.Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        /// <summary>
        /// Serializes a FilterConfiguration to JSON
        /// </summary>
        public static string SerializeFilter(FilterConfiguration config)
        {
            var wrapper = new FilterConfigurationWrapper
            {
                Version = "1.0",
                CreatedDate = DateTime.Now,
                Description = $"Filter with {config.Items.Count} item(s)",
                Items = config.Items.Select(ConvertToSerializable).ToList()
            };

            return JsonConvert.SerializeObject(wrapper, _serializerSettings);
        }

        /// <summary>
        /// Deserializes JSON to a FilterConfiguration
        /// </summary>
        public static FilterConfiguration DeserializeFilter(string json)
        {
            var wrapper = JsonConvert.DeserializeObject<FilterConfigurationWrapper>(json, _serializerSettings);

            if (wrapper == null)
                return null;

            var config = new FilterConfiguration();

            foreach (var item in wrapper.Items)
            {
                var filterItem = ConvertFromSerializable(item);
                if (filterItem != null)
                    config.Items.Add(filterItem);
            }

            return config;
        }

        /// <summary>
        /// Converts a FilterItemBase to a serializable object
        /// </summary>
        private static FilterItemSerializable ConvertToSerializable(FilterItemBase item)
        {
            if (item is FilterCriterion criterion)
            {
                return new FilterItemSerializable
                {
                    ItemType = "Criterion",
                    Field = criterion.Field.ToString(),
                    Operator = criterion.Operator.ToString(),
                    Value = criterion.Value,
                    LogicalOperator = criterion.LogicalOperator.ToString(),
                    IsEnabled = criterion.IsEnabled
                };
            }
            else if (item is FilterGroup group)
            {
                return new FilterItemSerializable
                {
                    ItemType = "Group",
                    GroupName = group.GroupName,
                    LogicalOperator = group.LogicalOperator.ToString(),
                    IsEnabled = group.IsEnabled,
                    Items = group.Items.Select(ConvertToSerializable).ToList()
                };
            }

            return null;
        }

        /// <summary>
        /// Converts a serializable object back to a FilterItemBase
        /// </summary>
        private static FilterItemBase ConvertFromSerializable(FilterItemSerializable item)
        {
            if (item.ItemType == "Criterion")
            {
                return new FilterCriterion
                {
                    Field = Enum.TryParse<FilterField>(item.Field, out var field) ? field : FilterField.PlanName,
                    Operator = Enum.TryParse<FilterOperator>(item.Operator, out var op) ? op : FilterOperator.Contains,
                    Value = item.Value ?? "",
                    LogicalOperator = Enum.TryParse<LogicalOperator>(item.LogicalOperator, out var logic) ? logic : LogicalOperator.And,
                    IsEnabled = item.IsEnabled
                };
            }
            else if (item.ItemType == "Group")
            {
                var group = new FilterGroup
                {
                    GroupName = item.GroupName ?? "Group",
                    LogicalOperator = Enum.TryParse<LogicalOperator>(item.LogicalOperator, out var logic) ? logic : LogicalOperator.And,
                    IsEnabled = item.IsEnabled
                };

                if (item.Items != null)
                {
                    foreach (var subItem in item.Items)
                    {
                        var filterItem = ConvertFromSerializable(subItem);
                        if (filterItem != null)
                            group.Items.Add(filterItem);
                    }
                }

                return group;
            }

            return null;
        }
    }

    /// <summary>
    /// Wrapper class for filter configuration serialization
    /// </summary>
    public class FilterConfigurationWrapper
    {
        public string Version { get; set; }
        public DateTime CreatedDate { get; set; }
        public string Description { get; set; }
        public List<FilterItemSerializable> Items { get; set; } = new List<FilterItemSerializable>();
    }

    /// <summary>
    /// Serializable representation of a filter item
    /// </summary>
    public class FilterItemSerializable
    {
        public string ItemType { get; set; } // "Criterion" or "Group"

        // Criterion properties
        public string Field { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }

        // Group properties
        public string GroupName { get; set; }
        public List<FilterItemSerializable> Items { get; set; }

        // Common properties
        public string LogicalOperator { get; set; }
        public bool IsEnabled { get; set; }
    }

    #endregion
}
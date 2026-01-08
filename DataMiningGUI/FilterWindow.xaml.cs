using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DataBaseStructure.AriaBase;

namespace DataMiningGUI
{
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
        /// Observable collection of criteria for UI binding
        /// </summary>
        public ObservableCollection<FilterCriterion> Criteria { get; private set; }

        // Enum items for combo box binding
        public List<EnumItem<FilterField>> FieldItems { get; private set; }
        public List<EnumItem<FilterOperator>> OperatorItems { get; private set; }
        public List<EnumItem<LogicalOperator>> LogicalOperatorItems { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new FilterWindow
        /// </summary>
        /// <param name="currentConfig">The current filter configuration (can be null)</param>
        /// <param name="patients">List of patients for preview functionality</param>
        public FilterWindow(FilterConfiguration currentConfig, List<PatientClass> patients)
        {
            InitializeComponent();

            _patients = patients ?? new List<PatientClass>();

            // Initialize enum items for combo boxes
            FieldItems = EnumHelper.GetEnumItems<FilterField>();
            OperatorItems = EnumHelper.GetEnumItems<FilterOperator>();
            LogicalOperatorItems = EnumHelper.GetEnumItems<LogicalOperator>();

            // Initialize criteria collection
            Criteria = new ObservableCollection<FilterCriterion>();

            // Copy existing configuration or create new
            if (currentConfig != null && currentConfig.Criteria.Any())
            {
                foreach (var criterion in currentConfig.Criteria)
                {
                    Criteria.Add(criterion.Clone());
                }
            }

            // Bind to UI
            CriteriaItemsControl.ItemsSource = Criteria;

            // Update UI state
            UpdateEmptyState();
            UpdateFilterSummary();

            DataContext = this;
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

            Criteria.Add(criterion);
            UpdateEmptyState();
            UpdateFilterSummary();
        }

        /// <summary>
        /// Removes a filter criterion
        /// </summary>
        private void RemoveCriterion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FilterCriterion criterion)
            {
                Criteria.Remove(criterion);
                UpdateEmptyState();
                UpdateFilterSummary();
            }
        }

        /// <summary>
        /// Clears all filter criteria
        /// </summary>
        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (Criteria.Any())
            {
                var result = MessageBox.Show(
                    "Are you sure you want to clear all filter criteria?",
                    "Confirm Clear",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Criteria.Clear();
                    UpdateEmptyState();
                    UpdateFilterSummary();
                    PreviewCountText.Text = "0 matches";
                }
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
            FilterConfig = BuildFilterConfiguration();
            FilterConfig.IsActive = Criteria.Any(c => c.IsEnabled);
            FilterApplied = true;
            DialogResult = true;
            Close();
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
        /// Builds a FilterConfiguration from the current criteria
        /// </summary>
        private FilterConfiguration BuildFilterConfiguration()
        {
            var config = new FilterConfiguration();

            foreach (var criterion in Criteria)
            {
                config.Criteria.Add(criterion.Clone());
            }

            return config;
        }

        /// <summary>
        /// Updates the empty state text visibility
        /// </summary>
        private void UpdateEmptyState()
        {
            EmptyStateText.Visibility = Criteria.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Updates the filter summary text
        /// </summary>
        private void UpdateFilterSummary()
        {
            if (!Criteria.Any())
            {
                FilterSummaryText.Text = "No filter criteria defined";
                return;
            }

            var enabledCount = Criteria.Count(c => c.IsEnabled);
            if (enabledCount == 0)
            {
                FilterSummaryText.Text = "All criteria disabled";
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"{enabledCount} criterion/criteria active: ");

            var summaryParts = new List<string>();
            foreach (var criterion in Criteria.Where(c => c.IsEnabled).Take(3))
            {
                summaryParts.Add($"{EnumHelper.GetDescription(criterion.Field)} {EnumHelper.GetDescription(criterion.Operator)} '{criterion.Value}'");
            }

            sb.Append(string.Join(" → ", summaryParts));

            if (enabledCount > 3)
            {
                sb.Append($" (+ {enabledCount - 3} more)");
            }

            FilterSummaryText.Text = sb.ToString();
        }

        #endregion
    }
}

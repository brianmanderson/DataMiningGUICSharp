using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DataBaseStructure;
using DataBaseStructure.AriaBase;

namespace DataMiningGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Fields

        private readonly string _databasePath = @"C:\Users\BRA008\Modular_Projects\LocalDatabases";
        private const int MaxDisplayCount = 50;

        private List<PatientClass> _allPatients = new List<PatientClass>();
        private ObservableCollection<PatientDisplayItem> _displayItems = new ObservableCollection<PatientDisplayItem>();
        private Dictionary<string, List<string>> _folderJsonFiles = new Dictionary<string, List<string>>();
        private Dictionary<string, CheckBox> _folderCheckBoxes = new Dictionary<string, CheckBox>();

        private CancellationTokenSource _loadCancellationTokenSource;
        private readonly object _patientsLock = new object();

        // Filter configuration
        private FilterConfiguration _currentFilter = new FilterConfiguration();

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();
            PatientDataGrid.ItemsSource = _displayItems;
            InitializeFolderCheckBoxes();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Discovers folders containing JSON files and creates checkboxes for each
        /// </summary>
        private void InitializeFolderCheckBoxes()
        {
            try
            {
                if (!Directory.Exists(_databasePath))
                {
                    StatusText.Text = $"Database path not found: {_databasePath}";
                    return;
                }

                var directories = Directory.GetDirectories(_databasePath);

                foreach (var directory in directories.OrderBy(d => d))
                {
                    var folderName = Path.GetFileName(directory);
                    var jsonFiles = new List<string>();

                    // Check if this directory contains any JSON files (including subdirectories)
                    jsonFiles = AriaDataBaseJsonReader.ReturnPatientFileNames(
                        directory,
                        jsonFiles,
                        "*.json",
                        SearchOption.AllDirectories);

                    if (jsonFiles.Count > 0)
                    {
                        // Store the JSON files for this folder
                        _folderJsonFiles[folderName] = jsonFiles;

                        // Create checkbox for this folder
                        var checkBox = new CheckBox
                        {
                            Content = $"{folderName} ({jsonFiles.Count} files)",
                            Tag = folderName,
                            Style = (Style)FindResource("FolderCheckBoxStyle")
                        };

                        checkBox.Checked += FolderCheckBox_Changed;
                        checkBox.Unchecked += FolderCheckBox_Changed;

                        _folderCheckBoxes[folderName] = checkBox;
                        FolderCheckBoxPanel.Items.Add(checkBox);
                    }
                }

                if (_folderCheckBoxes.Count == 0)
                {
                    StatusText.Text = "No folders with JSON files found.";
                }
                else
                {
                    StatusText.Text = $"Found {_folderCheckBoxes.Count} folders with patient data. Select folders to load.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error initializing: {ex.Message}";
                MessageBox.Show($"Error scanning database folders:\n{ex.Message}",
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles checkbox checked/unchecked events to reload patients
        /// </summary>
        private async void FolderCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            await LoadPatientsAsync();
        }

        /// <summary>
        /// Handles search text changes for filtering
        /// </summary>
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDisplayedPatients();
        }

        /// <summary>
        /// Clears the search text box
        /// </summary>
        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Clear();
        }

        /// <summary>
        /// Opens the advanced filter window
        /// </summary>
        private void OpenFilter_Click(object sender, RoutedEventArgs e)
        {
            List<PatientClass> patientsSnapshot;
            lock (_patientsLock)
            {
                patientsSnapshot = _allPatients.ToList();
            }

            var filterWindow = new FilterWindow(_currentFilter, patientsSnapshot)
            {
                Owner = this
            };

            if (filterWindow.ShowDialog() == true && filterWindow.FilterApplied)
            {
                _currentFilter = filterWindow.FilterConfig;
                UpdateFilterUI();
                UpdateDisplayedPatients();
            }
        }

        /// <summary>
        /// Clears the current filter
        /// </summary>
        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            _currentFilter.Clear();
            UpdateFilterUI();
            UpdateDisplayedPatients();
        }

        #endregion

        #region Patient Loading

        /// <summary>
        /// Asynchronously loads patients from all selected folders
        /// </summary>
        private async Task LoadPatientsAsync()
        {
            // Cancel any ongoing load operation
            _loadCancellationTokenSource?.Cancel();
            _loadCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _loadCancellationTokenSource.Token;

            // Get list of selected folders
            var selectedFolders = _folderCheckBoxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .Select(kvp => kvp.Key)
                .ToList();

            if (selectedFolders.Count == 0)
            {
                lock (_patientsLock)
                {
                    _allPatients.Clear();
                }
                _displayItems.Clear();
                TotalCountText.Text = "0";
                DisplayCountText.Text = "0";
                StatusText.Text = "No folders selected.";
                return;
            }

            // Show loading state
            SetLoadingState(true, $"Loading patients from {selectedFolders.Count} folder(s)...");

            try
            {
                // Collect all JSON files from selected folders
                var allJsonFiles = new List<string>();
                foreach (var folder in selectedFolders)
                {
                    if (_folderJsonFiles.TryGetValue(folder, out var files))
                    {
                        allJsonFiles.AddRange(files);
                    }
                }

                // Load patients asynchronously
                var loadedPatients = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return AriaDataBaseJsonReader.ReadPatientFiles(allJsonFiles);
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                lock (_patientsLock)
                {
                    _allPatients = loadedPatients;
                }

                // Update display on UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateDisplayedPatients();
                    StatusText.Text = $"Loaded {_allPatients.Count} patients from {selectedFolders.Count} folder(s).";
                });
            }
            catch (OperationCanceledException)
            {
                // Loading was cancelled, ignore
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"Error loading patients: {ex.Message}";
                    MessageBox.Show($"Error loading patients:\n{ex.Message}",
                        "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => SetLoadingState(false));
            }
        }

        #endregion

        #region Display Updates

        /// <summary>
        /// Updates the displayed patient list based on current patients, search filter, and advanced filter
        /// </summary>
        private void UpdateDisplayedPatients()
        {
            _displayItems.Clear();

            List<PatientClass> patientsSnapshot;
            lock (_patientsLock)
            {
                patientsSnapshot = _allPatients.ToList();
            }

            var searchText = SearchTextBox.Text?.Trim() ?? string.Empty;
            var filteredItems = new List<PatientDisplayItem>();

            foreach (var patient in patientsSnapshot)
            {
                // Apply MRN search filter if provided
                if (!string.IsNullOrEmpty(searchText))
                {
                    if (patient.MRN == null ||
                        patient.MRN.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                // Process patient courses and plans
                if (patient.Courses != null && patient.Courses.Any())
                {
                    foreach (var course in patient.Courses)
                    {
                        if (course.TreatmentPlans != null && course.TreatmentPlans.Any())
                        {
                            foreach (var plan in course.TreatmentPlans)
                            {
                                // Get BeamSets for additional info
                                var beamSets = plan.BeamSets;
                                var firstBeamSet = beamSets?.FirstOrDefault();

                                // Apply advanced filter
                                var context = new FilterContext(patient, course, plan, firstBeamSet);
                                if (!PatientFilterEngine.Matches(_currentFilter, context))
                                    continue;

                                filteredItems.Add(CreateDisplayItem(patient, course, plan, firstBeamSet));
                            }
                        }
                        else
                        {
                            // Course exists but has no plans - check filter
                            var context = new FilterContext(patient, course);
                            if (!PatientFilterEngine.Matches(_currentFilter, context))
                                continue;

                            filteredItems.Add(new PatientDisplayItem
                            {
                                MRN = patient.MRN,
                                PatientName = FormatPatientName(patient.Name_First, patient.Name_Last),
                                CourseName = course.Name,
                                PlanName = "(No plans)",
                                PlanType = "N/A",
                                MachineName = "N/A",
                                NumberOfFractions = "N/A",
                                DosePerFraction = "N/A",
                                TotalDose = "N/A",
                                PlannedBy = "N/A",
                                ApprovalStatus = "N/A",
                                ReviewDateTime = "N/A",
                                ReviewDateTimeSortable = DateTime.MinValue
                            });
                        }
                    }
                }
                else
                {
                    // Patient has no courses - check filter
                    var context = new FilterContext(patient);
                    if (!PatientFilterEngine.Matches(_currentFilter, context))
                        continue;

                    filteredItems.Add(new PatientDisplayItem
                    {
                        MRN = patient.MRN,
                        PatientName = FormatPatientName(patient.Name_First, patient.Name_Last),
                        CourseName = "(No courses)",
                        PlanName = "(No plans)",
                        PlanType = "N/A",
                        MachineName = "N/A",
                        NumberOfFractions = "N/A",
                        DosePerFraction = "N/A",
                        TotalDose = "N/A",
                        PlannedBy = "N/A",
                        ApprovalStatus = "N/A",
                        ReviewDateTime = "N/A",
                        ReviewDateTimeSortable = DateTime.MinValue
                    });
                }
            }

            // Sort by review date (most recent first) and take first 50
            var displayList = filteredItems
                .OrderByDescending(item => item.ReviewDateTimeSortable)
                .Take(MaxDisplayCount)
                .ToList();

            foreach (var item in displayList)
            {
                _displayItems.Add(item);
            }

            // Update counts
            TotalCountText.Text = filteredItems.Count.ToString();
            DisplayCountText.Text = _displayItems.Count.ToString();
        }

        /// <summary>
        /// Creates a display item from patient/course/plan/beamset data
        /// </summary>
        private PatientDisplayItem CreateDisplayItem(PatientClass patient, CourseClass course,
            TreatmentPlanClass plan, BeamSetClass beamSet)
        {
            // Extract dose info
            var normalization = beamSet?.PlanNormalization;
            var prescriptionTarget = beamSet?.Prescription?.PrescriptionTargets?.FirstOrDefault();

            int? fractions = normalization?.NumberOfFractions > 0 ? normalization.NumberOfFractions
                : prescriptionTarget?.NumberOfFractions > 0 ? prescriptionTarget.NumberOfFractions
                : (int?)null;

            double? dosePerFx = normalization?.Dose_per_Fraction > 0 ? normalization.Dose_per_Fraction
                : prescriptionTarget?.DosePerFraction > 0 ? prescriptionTarget.DosePerFraction
                : (double?)null;

            double? totalDose = (fractions.HasValue && dosePerFx.HasValue)
                ? fractions.Value * dosePerFx.Value
                : normalization?.DoseValue_cGy > 0 ? normalization.DoseValue_cGy
                : (double?)null;

            return new PatientDisplayItem
            {
                MRN = patient.MRN,
                PatientName = FormatPatientName(patient.Name_First, patient.Name_Last),
                CourseName = course.Name,
                PlanName = plan.PlanName,
                PlanType = plan.PlanType ?? "N/A",
                MachineName = beamSet?.MachineName ?? "N/A",
                NumberOfFractions = fractions?.ToString() ?? "N/A",
                DosePerFraction = dosePerFx?.ToString("F1") ?? "N/A",
                TotalDose = totalDose?.ToString("F1") ?? "N/A",
                PlannedBy = plan.PlannedBy ?? "N/A",
                ApprovalStatus = plan.Review?.ApprovalStatus ?? "N/A",
                ReviewDateTime = FormatReviewDateTime(plan.Review?.ReviewTime),
                ReviewDateTimeSortable = GetSortableDateTime(plan.Review?.ReviewTime)
            };
        }

        /// <summary>
        /// Updates the filter UI elements based on current filter state
        /// </summary>
        private void UpdateFilterUI()
        {
            bool hasActiveFilter = _currentFilter.IsActive && _currentFilter.HasCriteria;

            ActiveFilterBorder.Visibility = hasActiveFilter ? Visibility.Visible : Visibility.Collapsed;
            ClearFilterButton.Visibility = hasActiveFilter ? Visibility.Visible : Visibility.Collapsed;

            if (hasActiveFilter)
            {
                ActiveFilterText.Text = BuildFilterSummary();
            }
        }

        /// <summary>
        /// Builds a summary string of the current filter
        /// </summary>
        private string BuildFilterSummary()
        {
            if (!_currentFilter.HasCriteria)
                return string.Empty;

            var sb = new StringBuilder();
            var enabledCriteria = _currentFilter.Criteria.Where(c => c.IsEnabled).ToList();

            for (int i = 0; i < enabledCriteria.Count && i < 3; i++)
            {
                var c = enabledCriteria[i];
                if (i > 0)
                {
                    var prevLogic = enabledCriteria[i - 1].LogicalOperator;
                    sb.Append(prevLogic == LogicalOperator.And ? " AND " : " OR ");
                }
                sb.Append($"{EnumHelper.GetDescription(c.Field)} {EnumHelper.GetDescription(c.Operator)} '{c.Value}'");
            }

            if (enabledCriteria.Count > 3)
            {
                sb.Append($" (+{enabledCriteria.Count - 3} more)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Sets the loading state of the UI
        /// </summary>
        private void SetLoadingState(bool isLoading, string message = null)
        {
            LoadingProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(message))
            {
                StatusText.Text = message;
            }
            else if (!isLoading)
            {
                StatusText.Text = "Ready";
            }

            // Disable checkboxes while loading
            foreach (var checkBox in _folderCheckBoxes.Values)
            {
                checkBox.IsEnabled = !isLoading;
            }

            // Disable filter buttons while loading
            OpenFilterButton.IsEnabled = !isLoading;
            ClearFilterButton.IsEnabled = !isLoading;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Formats patient name from first and last name
        /// </summary>
        private static string FormatPatientName(string firstName, string lastName)
        {
            var first = string.IsNullOrWhiteSpace(firstName) ? "" : firstName.Trim();
            var last = string.IsNullOrWhiteSpace(lastName) ? "" : lastName.Trim();

            if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last))
                return "Unknown";

            if (string.IsNullOrEmpty(first))
                return last;

            if (string.IsNullOrEmpty(last))
                return first;

            return $"{last}, {first}";
        }

        /// <summary>
        /// Formats review datetime for display
        /// </summary>
        private static string FormatReviewDateTime(DateTimeClass dateTime)
        {
            if (dateTime == null)
                return "N/A";

            try
            {
                return $"{dateTime.Month:D2}/{dateTime.Day:D2}/{dateTime.Year} {dateTime.Hour:D2}:{dateTime.Minute:D2}";
            }
            catch
            {
                return "Invalid Date";
            }
        }

        /// <summary>
        /// Converts DateTimeClass to sortable DateTime
        /// </summary>
        private static DateTime GetSortableDateTime(DateTimeClass dateTime)
        {
            if (dateTime == null)
                return DateTime.MinValue;

            try
            {
                return new DateTime(
                    dateTime.Year,
                    dateTime.Month,
                    dateTime.Day,
                    dateTime.Hour,
                    dateTime.Minute,
                    dateTime.Second);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        #endregion
    }

    #region Display Model

    /// <summary>
    /// Flattened display model for patient/course/plan data
    /// </summary>
    public class PatientDisplayItem : INotifyPropertyChanged
    {
        private string _mrn;
        private string _patientName;
        private string _courseName;
        private string _planName;
        private string _planType;
        private string _machineName;
        private string _numberOfFractions;
        private string _dosePerFraction;
        private string _totalDose;
        private string _plannedBy;
        private string _approvalStatus;
        private string _reviewDateTime;
        private DateTime _reviewDateTimeSortable;

        public string MRN
        {
            get => _mrn;
            set { _mrn = value; OnPropertyChanged(nameof(MRN)); }
        }

        public string PatientName
        {
            get => _patientName;
            set { _patientName = value; OnPropertyChanged(nameof(PatientName)); }
        }

        public string CourseName
        {
            get => _courseName;
            set { _courseName = value; OnPropertyChanged(nameof(CourseName)); }
        }

        public string PlanName
        {
            get => _planName;
            set { _planName = value; OnPropertyChanged(nameof(PlanName)); }
        }

        public string PlanType
        {
            get => _planType;
            set { _planType = value; OnPropertyChanged(nameof(PlanType)); }
        }

        public string MachineName
        {
            get => _machineName;
            set { _machineName = value; OnPropertyChanged(nameof(MachineName)); }
        }

        public string NumberOfFractions
        {
            get => _numberOfFractions;
            set { _numberOfFractions = value; OnPropertyChanged(nameof(NumberOfFractions)); }
        }

        public string DosePerFraction
        {
            get => _dosePerFraction;
            set { _dosePerFraction = value; OnPropertyChanged(nameof(DosePerFraction)); }
        }

        public string TotalDose
        {
            get => _totalDose;
            set { _totalDose = value; OnPropertyChanged(nameof(TotalDose)); }
        }

        public string PlannedBy
        {
            get => _plannedBy;
            set { _plannedBy = value; OnPropertyChanged(nameof(PlannedBy)); }
        }

        public string ApprovalStatus
        {
            get => _approvalStatus;
            set { _approvalStatus = value; OnPropertyChanged(nameof(ApprovalStatus)); }
        }

        public string ReviewDateTime
        {
            get => _reviewDateTime;
            set { _reviewDateTime = value; OnPropertyChanged(nameof(ReviewDateTime)); }
        }

        public DateTime ReviewDateTimeSortable
        {
            get => _reviewDateTimeSortable;
            set { _reviewDateTimeSortable = value; OnPropertyChanged(nameof(ReviewDateTimeSortable)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion
}

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

        // Pagination fields
        private int _currentPage = 1;
        private int _totalPages = 1;
        private List<PatientDisplayItem> _allFilteredItems = new List<PatientDisplayItem>();

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

                foreach (var directory in directories.OrderByDescending(d => d))
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

                        // Update selected files count when checkbox changes
                        checkBox.Checked += FolderCheckBox_SelectionChanged;
                        checkBox.Unchecked += FolderCheckBox_SelectionChanged;

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
                    StatusText.Text = $"Found {_folderCheckBoxes.Count} folders with patient data. Select folders and click 'Load Patients'.";
                }

                UpdateSelectedFilesCount();
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
        /// Updates the selected files count when checkboxes change
        /// </summary>
        private void FolderCheckBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateSelectedFilesCount();
        }

        /// <summary>
        /// Handles the Load Patients button click
        /// </summary>
        private async void LoadPatients_Click(object sender, RoutedEventArgs e)
        {
            await LoadPatientsAsync();
        }

        /// <summary>
        /// Handles the Cancel Load button click
        /// </summary>
        private void CancelLoad_Click(object sender, RoutedEventArgs e)
        {
            _loadCancellationTokenSource?.Cancel();
            StatusText.Text = "Load cancelled.";
        }

        /// <summary>
        /// Handles search text changes for filtering
        /// </summary>
        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentPage = 1; // Reset to first page on new search
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
                _currentPage = 1; // Reset to first page when filter changes
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
            _currentPage = 1; // Reset to first page when filter is cleared
            UpdateFilterUI();
            UpdateDisplayedPatients();
        }

        /// <summary>
        /// Opens the export window to export displayed data to CSV
        /// </summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            // Get all currently filtered items (not just the current page)
            var dataToExport = _allFilteredItems.ToList();

            if (dataToExport.Count == 0)
            {
                MessageBox.Show("No data to export. Please load patients and apply any desired filters first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var exportWindow = new ExportWindow(dataToExport)
            {
                Owner = this
            };

            exportWindow.ShowDialog();
        }

        #endregion

        #region Pagination Event Handlers

        /// <summary>
        /// Navigates to the first page
        /// </summary>
        private void FirstPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage = 1;
                UpdatePageDisplay();
            }
        }

        /// <summary>
        /// Navigates to the previous page
        /// </summary>
        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                UpdatePageDisplay();
            }
        }

        /// <summary>
        /// Navigates to the next page
        /// </summary>
        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage++;
                UpdatePageDisplay();
            }
        }

        /// <summary>
        /// Navigates to the last page
        /// </summary>
        private void LastPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages)
            {
                _currentPage = _totalPages;
                UpdatePageDisplay();
            }
        }

        #endregion

        #region Patient Loading

        /// <summary>
        /// Updates the display of selected files count
        /// </summary>
        private void UpdateSelectedFilesCount()
        {
            var selectedFolders = _folderCheckBoxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .Select(kvp => kvp.Key)
                .ToList();

            int totalFiles = 0;
            foreach (var folder in selectedFolders)
            {
                if (_folderJsonFiles.TryGetValue(folder, out var files))
                {
                    totalFiles += files.Count;
                }
            }

            SelectedFilesText.Text = $"{totalFiles} files selected from {selectedFolders.Count} folder(s)";
        }

        /// <summary>
        /// Asynchronously loads patients from all selected folders with progress reporting
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
                MessageBox.Show("Please select at least one folder to load patients from.",
                    "No Folders Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Show loading state
            SetLoadingState(true);

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

                // Create progress reporter
                var progress = new Progress<PatientLoadProgress>(OnLoadProgressChanged);

                // Load patients asynchronously with progress
                var loadedPatients = await AriaDataBaseJsonReader.ReadPatientFilesAsync(
                    allJsonFiles,
                    progress,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                lock (_patientsLock)
                {
                    _allPatients = loadedPatients;
                }

                // Reset pagination and update display on UI thread
                _currentPage = 1;
                UpdateDisplayedPatients();

                // Show loaded info
                LoadedInfoBorder.Visibility = Visibility.Visible;
                LoadedInfoText.Text = $"Loaded {_allPatients.Count} patients from {selectedFolders.Count} folder(s) ({allJsonFiles.Count} files)";
                StatusText.Text = $"Ready - {_allPatients.Count} patients loaded";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Load cancelled by user.";
                LoadedInfoBorder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading patients: {ex.Message}";
                MessageBox.Show($"Error loading patients:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetLoadingState(false);
            }
        }

        /// <summary>
        /// Handles progress updates from the patient loader
        /// </summary>
        private void OnLoadProgressChanged(PatientLoadProgress progress)
        {
            LoadProgressBar.Value = progress.PercentComplete;
            LoadProgressText.Text = $"{progress.ProcessedFiles}/{progress.TotalFiles} ({progress.PercentComplete:F0}%) - {progress.SuccessfulFiles} loaded, {progress.ErrorFiles} errors";
        }

        #endregion

        #region Display Updates

        /// <summary>
        /// Updates the displayed patient list based on current patients, search filter, and advanced filter
        /// </summary>
        private void UpdateDisplayedPatients()
        {
            _displayItems.Clear();
            _allFilteredItems.Clear();

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
                                BeamSetClass firstBeamSet = plan.BeamSet;

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
                                Energy = "N/A",
                                Technique = "N/A",
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
                        Energy = "N/A",
                        Technique = "N/A",
                        PlannedBy = "N/A",
                        ApprovalStatus = "N/A",
                        ReviewDateTime = "N/A",
                        ReviewDateTimeSortable = DateTime.MinValue
                    });
                }
            }

            // Sort by review date (most recent first)
            _allFilteredItems = filteredItems
                .OrderByDescending(item => item.ReviewDateTimeSortable)
                .ToList();

            // Calculate total pages
            _totalPages = _allFilteredItems.Count > 0
                ? (int)Math.Ceiling((double)_allFilteredItems.Count / MaxDisplayCount)
                : 1;

            // Ensure current page is valid
            if (_currentPage > _totalPages)
                _currentPage = _totalPages;
            if (_currentPage < 1)
                _currentPage = 1;

            // Update pagination controls visibility
            UpdatePaginationControls();

            // Display current page
            UpdatePageDisplay();
        }

        /// <summary>
        /// Updates the display with the current page of items
        /// </summary>
        private void UpdatePageDisplay()
        {
            _displayItems.Clear();

            // Calculate the range of items to display
            int startIndex = (_currentPage - 1) * MaxDisplayCount;
            int endIndex = Math.Min(startIndex + MaxDisplayCount, _allFilteredItems.Count);

            // Add items for the current page
            for (int i = startIndex; i < endIndex; i++)
            {
                _displayItems.Add(_allFilteredItems[i]);
            }

            // Update counts and page info
            TotalCountText.Text = _allFilteredItems.Count.ToString();
            DisplayCountText.Text = _displayItems.Count.ToString();
            CurrentPageText.Text = _currentPage.ToString();
            TotalPagesText.Text = _totalPages.ToString();

            // Update button states
            FirstPageButton.IsEnabled = _currentPage > 1;
            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < _totalPages;
            LastPageButton.IsEnabled = _currentPage < _totalPages;
        }

        /// <summary>
        /// Updates the visibility and state of pagination controls
        /// </summary>
        private void UpdatePaginationControls()
        {
            // Show pagination only if there are more items than can fit on one page
            bool showPagination = _allFilteredItems.Count > MaxDisplayCount;
            PaginationPanel.Visibility = showPagination ? Visibility.Visible : Visibility.Collapsed;
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

            // Extract Energy and Technique info
            var energy = "N/A";
            var technique = "N/A";
            
            if (beamSet?.Beams != null && beamSet.Beams.Any())
            {
                var firstBeam = beamSet.Beams.FirstOrDefault();
                energy = firstBeam?.BeamQualityId ?? "N/A";
                technique = firstBeam?.DeliveryTechnique ?? "N/A";
            }

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
                Energy = energy,
                Technique = technique,
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
            bool hasActiveFilter = _currentFilter.IsActive && _currentFilter.HasItems;

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
            if (!_currentFilter.HasItems)
                return string.Empty;

            var sb = new StringBuilder();
            var enabledItems = _currentFilter.Items.Where(i => i.IsEnabled).ToList();

            for (int i = 0; i < enabledItems.Count && i < 3; i++)
            {
                var item = enabledItems[i];
                if (i > 0)
                {
                    var prevLogic = enabledItems[i - 1].LogicalOperator;
                    sb.Append(prevLogic == LogicalOperator.And ? " AND " : " OR ");
                }
                sb.Append(item.DisplaySummary);
            }

            if (enabledItems.Count > 3)
            {
                sb.Append($" (+{enabledItems.Count - 3} more)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Sets the loading state of the UI
        /// </summary>
        private void SetLoadingState(bool isLoading)
        {
            LoadPatientsButton.IsEnabled = !isLoading;
            CancelLoadButton.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadProgressBar.Value = 0;

            if (isLoading)
            {
                LoadProgressText.Text = "Starting...";
                StatusText.Text = "Loading patients...";
            }
            else
            {
                LoadProgressText.Text = "";
            }

            // Disable/enable other controls while loading
            foreach (var checkBox in _folderCheckBoxes.Values)
            {
                checkBox.IsEnabled = !isLoading;
            }

            OpenFilterButton.IsEnabled = !isLoading;
            ClearFilterButton.IsEnabled = !isLoading;
            SearchTextBox.IsEnabled = !isLoading;
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
        private string _energy;
        private string _technique;

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

        public string Energy
        {
            get => _energy;
            set { _energy = value; OnPropertyChanged(nameof(Energy)); }
        }

        public string Technique
        {
            get => _technique;
            set { _technique = value; OnPropertyChanged(nameof(Technique)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion
}
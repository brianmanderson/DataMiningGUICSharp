using DataBaseStructure.AriaBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace DataMiningGUI
{
    /// <summary>
    /// Interaction logic for DicomExportWindow.xaml
    /// </summary>
    public partial class DicomExportWindow : Window
    {
        private ObservableCollection<ExportPatientItem> _patients;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isExporting = false;

        /// <summary>
        /// Constructor that accepts filtered PatientDisplayItems and full patient data
        /// </summary>
        public DicomExportWindow(List<PatientDisplayItem> filteredDisplayItems, List<PatientClass> allPatients)
        {
            InitializeComponent();
            _patients = new ObservableCollection<ExportPatientItem>();
            PatientTreeView.ItemsSource = _patients;

            LoadPatientsFromDisplayItems(filteredDisplayItems, allPatients);
        }

        /// <summary>
        /// Default constructor for XAML designer
        /// </summary>
        public DicomExportWindow()
        {
            InitializeComponent();
            _patients = new ObservableCollection<ExportPatientItem>();
            PatientTreeView.ItemsSource = _patients;
        }

        /// <summary>
        /// Load patients by cross-referencing filtered display items with full patient data
        /// </summary>
        private void LoadPatientsFromDisplayItems(List<PatientDisplayItem> filteredDisplayItems, List<PatientClass> allPatients)
        {
            _patients.Clear();

            // Get unique MRNs from filtered items
            HashSet<string> filteredMRNs = new HashSet<string>(
                filteredDisplayItems.Select(d => d.MRN).Distinct());

            // Get unique Course names per MRN from filtered items
            Dictionary<string, HashSet<string>> mrnToCourses = new Dictionary<string, HashSet<string>>();
            foreach (PatientDisplayItem item in filteredDisplayItems)
            {
                if (!mrnToCourses.ContainsKey(item.MRN))
                {
                    mrnToCourses[item.MRN] = new HashSet<string>();
                }
                if (!string.IsNullOrEmpty(item.CourseName))
                {
                    mrnToCourses[item.MRN].Add(item.CourseName);
                }
            }

            // Filter patients to only those in the filtered list
            foreach (PatientClass patient in allPatients)
            {
                if (!filteredMRNs.Contains(patient.MRN))
                {
                    continue;
                }

                ExportPatientItem exportPatient = new ExportPatientItem
                {
                    MRN = patient.MRN,
                    FirstName = patient.Name_First,
                    LastName = patient.Name_Last,
                    PatientData = patient
                };

                // Build lookup for examinations by FrameOfReference
                Dictionary<string, ExaminationClass> frameOfRefToExam = new Dictionary<string, ExaminationClass>();
                if (patient.Examinations != null)
                {
                    foreach (ExaminationClass exam in patient.Examinations)
                    {
                        string frameOfRef = GetFrameOfReference(exam);
                        if (!string.IsNullOrEmpty(frameOfRef) && !frameOfRefToExam.ContainsKey(frameOfRef))
                        {
                            frameOfRefToExam[frameOfRef] = exam;
                        }
                    }
                }

                // Get the courses that were in the filtered results for this patient
                HashSet<string> filteredCourseNames = mrnToCourses.ContainsKey(patient.MRN)
                    ? mrnToCourses[patient.MRN]
                    : new HashSet<string>();

                if (patient.Courses != null)
                {
                    foreach (CourseClass course in patient.Courses)
                    {
                        // Only include courses that were in the filtered results
                        if (filteredCourseNames.Count > 0 && !filteredCourseNames.Contains(course.Name))
                        {
                            continue;
                        }

                        ExportCourseItem exportCourse = new ExportCourseItem
                        {
                            CourseName = course.Name,
                            CourseData = course,
                            Parent = exportPatient
                        };

                        // Get examinations referenced by plans in this course
                        HashSet<string> referencedExamNames = new HashSet<string>();
                        if (course.TreatmentPlans != null)
                        {
                            foreach (TreatmentPlanClass plan in course.TreatmentPlans)
                            {
                                if (!string.IsNullOrEmpty(plan.Referenced_Exam_Name))
                                {
                                    referencedExamNames.Add(plan.Referenced_Exam_Name);
                                }
                            }
                        }

                        // Add examinations
                        if (patient.Examinations != null)
                        {
                            foreach (ExaminationClass exam in patient.Examinations)
                            {
                                if (referencedExamNames.Count == 0 || referencedExamNames.Contains(exam.ExamName))
                                {
                                    List<TreatmentPlanClass> associatedPlans = new List<TreatmentPlanClass>();
                                    if (course.TreatmentPlans != null)
                                    {
                                        associatedPlans = course.TreatmentPlans
                                            .Where(p => p.Referenced_Exam_Name == exam.ExamName)
                                            .ToList();
                                    }

                                    string examFrameOfRef = GetFrameOfReference(exam);

                                    // Find registrations where this exam is the target (ToFrameOfReference)
                                    List<RegistrationExportInfo> associatedRegistrations =
                                        FindAssociatedRegistrations(patient, exam, examFrameOfRef, frameOfRefToExam);

                                    ExportExaminationItem exportExam = new ExportExaminationItem
                                    {
                                        ExamName = exam.ExamName,
                                        SeriesInstanceUID = exam.SeriesInstanceUID,
                                        StudyInstanceUID = exam.StudyInstanceUID,
                                        FrameOfReferenceUID = examFrameOfRef,
                                        ExamData = exam,
                                        AssociatedPlans = associatedPlans,
                                        AssociatedRegistrations = associatedRegistrations,
                                        Parent = exportCourse
                                    };

                                    exportCourse.Examinations.Add(exportExam);
                                }
                            }
                        }

                        if (exportCourse.Examinations.Count > 0)
                        {
                            exportPatient.Courses.Add(exportCourse);
                        }
                    }
                }

                if (exportPatient.Courses.Count > 0)
                {
                    _patients.Add(exportPatient);
                }
            }

            UpdateSelectionSummary();
        }

        /// <summary>
        /// Get the Frame of Reference UID from an examination
        /// </summary>
        private string GetFrameOfReference(ExaminationClass exam)
        {
            if (exam == null)
            {
                return null;
            }
            if (exam.EquipmentInfo != null && !string.IsNullOrEmpty(exam.EquipmentInfo.FrameOfReference))
            {
                return exam.EquipmentInfo.FrameOfReference;
            }
            return null;
        }

        /// <summary>
        /// Find registrations where the given examination is the target (ToFrameOfReference matches)
        /// and resolve the source examinations
        /// </summary>
        private List<RegistrationExportInfo> FindAssociatedRegistrations(
            PatientClass patient,
            ExaminationClass targetExam,
            string targetFrameOfRef,
            Dictionary<string, ExaminationClass> frameOfRefToExam)
        {
            List<RegistrationExportInfo> result = new List<RegistrationExportInfo>();

            if (patient.Registrations == null || string.IsNullOrEmpty(targetFrameOfRef))
            {
                return result;
            }

            foreach (RegistrationClass registration in patient.Registrations)
            {
                // Check if this exam is the target of the registration
                if (registration.ToFrameOfReference == targetFrameOfRef)
                {
                    // Find the source examination by FromFrameOfReference
                    ExaminationClass sourceExam = null;
                    if (!string.IsNullOrEmpty(registration.FromFrameOfReference) &&
                        frameOfRefToExam.ContainsKey(registration.FromFrameOfReference))
                    {
                        sourceExam = frameOfRefToExam[registration.FromFrameOfReference];
                    }

                    RegistrationExportInfo regInfo = new RegistrationExportInfo
                    {
                        RegistrationName = registration.Name,
                        RegistrationUID = registration.UID, // RegistrationClass doesn't have UID property
                        FromFrameOfReference = registration.FromFrameOfReference,
                        ToFrameOfReference = registration.ToFrameOfReference,
                        RegistrationData = registration
                    };

                    if (sourceExam != null)
                    {
                        regInfo.SourceExamName = sourceExam.ExamName;
                        regInfo.SourceSeriesInstanceUID = sourceExam.SeriesInstanceUID;
                        regInfo.SourceStudyInstanceUID = sourceExam.StudyInstanceUID;
                        regInfo.SourceExamData = sourceExam;
                    }

                    result.Add(regInfo);
                }
            }

            return result;
        }

        /// <summary>
        /// Alternative: Initialize the window directly with PatientClass data
        /// </summary>
        public void LoadPatients(IEnumerable<PatientClass> patients)
        {
            _patients.Clear();

            foreach (PatientClass patient in patients)
            {
                ExportPatientItem exportPatient = new ExportPatientItem
                {
                    MRN = patient.MRN,
                    FirstName = patient.Name_First,
                    LastName = patient.Name_Last,
                    PatientData = patient
                };

                // Build lookup for examinations by FrameOfReference
                Dictionary<string, ExaminationClass> frameOfRefToExam = new Dictionary<string, ExaminationClass>();
                if (patient.Examinations != null)
                {
                    foreach (ExaminationClass exam in patient.Examinations)
                    {
                        string frameOfRef = GetFrameOfReference(exam);
                        if (!string.IsNullOrEmpty(frameOfRef) && !frameOfRefToExam.ContainsKey(frameOfRef))
                        {
                            frameOfRefToExam[frameOfRef] = exam;
                        }
                    }
                }

                if (patient.Courses != null)
                {
                    foreach (CourseClass course in patient.Courses)
                    {
                        ExportCourseItem exportCourse = new ExportCourseItem
                        {
                            CourseName = course.Name,
                            CourseData = course,
                            Parent = exportPatient
                        };

                        HashSet<string> referencedExamNames = new HashSet<string>();
                        if (course.TreatmentPlans != null)
                        {
                            foreach (TreatmentPlanClass plan in course.TreatmentPlans)
                            {
                                if (!string.IsNullOrEmpty(plan.Referenced_Exam_Name))
                                {
                                    referencedExamNames.Add(plan.Referenced_Exam_Name);
                                }
                            }
                        }

                        if (patient.Examinations != null)
                        {
                            foreach (ExaminationClass exam in patient.Examinations)
                            {
                                if (referencedExamNames.Count == 0 || referencedExamNames.Contains(exam.ExamName))
                                {
                                    List<TreatmentPlanClass> associatedPlans = new List<TreatmentPlanClass>();
                                    if (course.TreatmentPlans != null)
                                    {
                                        associatedPlans = course.TreatmentPlans
                                            .Where(p => p.Referenced_Exam_Name == exam.ExamName)
                                            .ToList();
                                    }

                                    string examFrameOfRef = GetFrameOfReference(exam);
                                    List<RegistrationExportInfo> associatedRegistrations =
                                        FindAssociatedRegistrations(patient, exam, examFrameOfRef, frameOfRefToExam);

                                    ExportExaminationItem exportExam = new ExportExaminationItem
                                    {
                                        ExamName = exam.ExamName,
                                        SeriesInstanceUID = exam.SeriesInstanceUID,
                                        StudyInstanceUID = exam.StudyInstanceUID,
                                        FrameOfReferenceUID = examFrameOfRef,
                                        ExamData = exam,
                                        AssociatedPlans = associatedPlans,
                                        AssociatedRegistrations = associatedRegistrations,
                                        Parent = exportCourse
                                    };

                                    exportCourse.Examinations.Add(exportExam);
                                }
                            }
                        }

                        if (exportCourse.Examinations.Count > 0)
                        {
                            exportPatient.Courses.Add(exportCourse);
                        }
                    }
                }

                if (exportPatient.Courses.Count > 0)
                {
                    _patients.Add(exportPatient);
                }
            }

            UpdateSelectionSummary();
        }

        #region Selection Event Handlers

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = SelectAllCheckBox.IsChecked.HasValue && SelectAllCheckBox.IsChecked.Value;
            foreach (ExportPatientItem patient in _patients)
            {
                patient.IsSelected = isChecked;
                foreach (ExportCourseItem course in patient.Courses)
                {
                    course.IsSelected = isChecked;
                    foreach (ExportExaminationItem exam in course.Examinations)
                    {
                        exam.IsSelected = isChecked;
                    }
                }
            }
            UpdateSelectionSummary();
        }

        private void PatientCheckBox_Click(object sender, RoutedEventArgs e)
        {
            CheckBox checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                ExportPatientItem patient = checkBox.DataContext as ExportPatientItem;
                if (patient != null)
                {
                    bool isChecked = patient.IsSelected;
                    foreach (ExportCourseItem course in patient.Courses)
                    {
                        course.IsSelected = isChecked;
                        foreach (ExportExaminationItem exam in course.Examinations)
                        {
                            exam.IsSelected = isChecked;
                        }
                    }
                }
            }
            UpdateSelectAllState();
            UpdateSelectionSummary();
        }

        private void CourseCheckBox_Click(object sender, RoutedEventArgs e)
        {
            CheckBox checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                ExportCourseItem course = checkBox.DataContext as ExportCourseItem;
                if (course != null)
                {
                    bool isChecked = course.IsSelected;
                    foreach (ExportExaminationItem exam in course.Examinations)
                    {
                        exam.IsSelected = isChecked;
                    }

                    if (course.Parent != null)
                    {
                        course.Parent.IsSelected = course.Parent.Courses.Any(c => c.IsSelected);
                    }
                }
            }
            UpdateSelectAllState();
            UpdateSelectionSummary();
        }

        private void ExaminationCheckBox_Click(object sender, RoutedEventArgs e)
        {
            CheckBox checkBox = sender as CheckBox;
            if (checkBox != null)
            {
                ExportExaminationItem exam = checkBox.DataContext as ExportExaminationItem;
                if (exam != null)
                {
                    if (exam.Parent != null)
                    {
                        exam.Parent.IsSelected = exam.Parent.Examinations.Any(ex => ex.IsSelected);

                        if (exam.Parent.Parent != null)
                        {
                            exam.Parent.Parent.IsSelected = exam.Parent.Parent.Courses.Any(c => c.IsSelected);
                        }
                    }
                }
            }
            UpdateSelectAllState();
            UpdateSelectionSummary();
        }

        private void UpdateSelectAllState()
        {
            if (_patients.Count == 0)
            {
                SelectAllCheckBox.IsChecked = false;
                return;
            }

            bool allSelected = _patients.All(p => p.Courses.All(c => c.Examinations.All(ex => ex.IsSelected)));
            bool noneSelected = _patients.All(p => p.Courses.All(c => c.Examinations.All(ex => !ex.IsSelected)));

            if (allSelected)
            {
                SelectAllCheckBox.IsChecked = true;
            }
            else if (noneSelected)
            {
                SelectAllCheckBox.IsChecked = false;
            }
            else
            {
                SelectAllCheckBox.IsChecked = null;
            }
        }

        private void UpdateSelectionSummary()
        {
            int selectedCount = _patients
                .SelectMany(p => p.Courses)
                .SelectMany(c => c.Examinations)
                .Count(ex => ex.IsSelected);

            SelectionSummaryText.Text = string.Format("{0} examination(s) selected", selectedCount);
        }

        #endregion

        #region TreeView Expansion

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetTreeViewExpansion(PatientTreeView, true);
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetTreeViewExpansion(PatientTreeView, false);
        }

        private void SetTreeViewExpansion(ItemsControl control, bool isExpanded)
        {
            foreach (object item in control.Items)
            {
                TreeViewItem container = control.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (container != null)
                {
                    container.IsExpanded = isExpanded;
                    SetTreeViewExpansion(container, isExpanded);
                }
            }
        }

        #endregion

        #region Export Handlers

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Export Folder";
                dialog.SelectedPath = ExportFolderTextBox.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ExportFolderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void ExportRegistrationsCheckBox_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (RegistrationModalityPanel != null)
            {
                bool isChecked = ExportRegistrationsCheckBox.IsChecked.HasValue &&
                                 ExportRegistrationsCheckBox.IsChecked.Value;
                RegistrationModalityPanel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting)
            {
                return;
            }

            List<ExportItem> selectedExams = GetSelectedExportItems();
            if (selectedExams.Count == 0)
            {
                MessageBox.Show("Please select at least one examination to export.",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string exportFolder = ExportFolderTextBox.Text.Trim();
            if (string.IsNullOrEmpty(exportFolder))
            {
                MessageBox.Show("Please specify an export folder.",
                    "No Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool exportExam = ExportExaminationCheckBox.IsChecked.HasValue && ExportExaminationCheckBox.IsChecked.Value;
            bool exportStruct = ExportStructureCheckBox.IsChecked.HasValue && ExportStructureCheckBox.IsChecked.Value;
            bool exportPlan = ExportPlanCheckBox.IsChecked.HasValue && ExportPlanCheckBox.IsChecked.Value;
            bool exportDose = ExportDoseCheckBox.IsChecked.HasValue && ExportDoseCheckBox.IsChecked.Value;
            bool exportRegs = ExportRegistrationsCheckBox.IsChecked.HasValue && ExportRegistrationsCheckBox.IsChecked.Value;

            if (!exportExam && !exportStruct && !exportPlan && !exportDose && !exportRegs)
            {
                MessageBox.Show("Please select at least one data type to export.",
                    "No Data Type", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int remotePort;
            if (!int.TryParse(RemotePortTextBox.Text, out remotePort))
            {
                MessageBox.Show("Invalid remote port number.",
                    "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int localPort;
            if (!int.TryParse(LocalPortTextBox.Text, out localPort))
            {
                MessageBox.Show("Invalid local port number.",
                    "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DicomExportOptions exportOptions = new DicomExportOptions
            {
                ExportFolder = exportFolder,
                ExportExamination = exportExam,
                ExportStructure = exportStruct,
                ExportPlan = exportPlan,
                ExportDose = exportDose,
                ExportRegistrations = exportRegs,
                ExportRegistrationsCT = ExportRegCTCheckBox.IsChecked.HasValue && ExportRegCTCheckBox.IsChecked.Value,
                ExportRegistrationsMR = ExportRegMRCheckBox.IsChecked.HasValue && ExportRegMRCheckBox.IsChecked.Value,
                ExportRegistrationsPET = ExportRegPETCheckBox.IsChecked.HasValue && ExportRegPETCheckBox.IsChecked.Value,
                ExportRegistrationsCBCT = ExportRegCBCTCheckBox.IsChecked.HasValue && ExportRegCBCTCheckBox.IsChecked.Value,
                Anonymize = AnonymizeCheckBox.IsChecked.HasValue && AnonymizeCheckBox.IsChecked.Value,
                RemoteAETitle = RemoteAETitleTextBox.Text.Trim(),
                RemoteIP = RemoteIPTextBox.Text.Trim(),
                RemotePort = remotePort,
                LocalAETitle = LocalAETitleTextBox.Text.Trim(),
                LocalPort = localPort
            };

            _isExporting = true;
            _cancellationTokenSource = new CancellationTokenSource();

            SetUIExportingState(true);

            try
            {
                DicomExportService exporter = new DicomExportService();
                Progress<DicomExportProgress> progress = new Progress<DicomExportProgress>(UpdateProgress);

                // FellowOakDicom's ExportAsync is already async, so await it directly
                await exporter.ExportAsync(
                    selectedExams,
                    exportOptions,
                    progress,
                    _cancellationTokenSource.Token);

                MessageBox.Show("Export completed successfully!",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Export was cancelled.",
                    "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Export failed: {0}", ex.Message),
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isExporting = false;
                SetUIExportingState(false);
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }

        private List<ExportItem> GetSelectedExportItems()
        {
            List<ExportItem> items = new List<ExportItem>();

            foreach (ExportPatientItem patient in _patients)
            {
                // Get all examinations for this patient
                List<ExaminationClass> patientExams = new List<ExaminationClass>();
                if (patient.PatientData != null && patient.PatientData.Examinations != null)
                {
                    patientExams = patient.PatientData.Examinations;
                }

                foreach (ExportCourseItem course in patient.Courses)
                {
                    foreach (ExportExaminationItem exam in course.Examinations)
                    {
                        if (exam.IsSelected)
                        {
                            items.Add(new ExportItem
                            {
                                MRN = patient.MRN,
                                PatientName = string.Format("{0}, {1}", patient.LastName, patient.FirstName),
                                CourseName = course.CourseName,
                                ExamName = exam.ExamName,
                                SeriesInstanceUID = exam.SeriesInstanceUID,
                                StudyInstanceUID = exam.StudyInstanceUID,
                                FrameOfReferenceUID = exam.FrameOfReferenceUID,
                                AssociatedPlans = exam.AssociatedPlans,
                                ExamData = exam.ExamData,
                                AssociatedRegistrations = exam.AssociatedRegistrations,
                                AssociatedExams = patientExams
                            });
                        }
                    }
                }
            }

            return items;
        }

        private void UpdateProgress(DicomExportProgress progress)
        {
            Dispatcher.Invoke(() =>
            {
                ExportProgressBar.Value = progress.PercentComplete;
                ProgressText.Text = progress.StatusMessage;
                ProgressDetailText.Text = progress.DetailMessage;
            });
        }

        private void SetUIExportingState(bool isExporting)
        {
            ExportButton.IsEnabled = !isExporting;
            CancelButton.Content = isExporting ? "Cancel Export" : "Cancel";
            ProgressPanel.Visibility = isExporting ? Visibility.Visible : Visibility.Collapsed;
            PatientTreeView.IsEnabled = !isExporting;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting)
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
            else
            {
                this.DialogResult = false;
                this.Close();
            }
        }

        #endregion
    }
}

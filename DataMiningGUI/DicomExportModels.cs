using DataBaseStructure.AriaBase;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace DataMiningGUI
{
    #region TreeView Item Classes (for UI binding)

    /// <summary>
    /// Represents a patient in the export TreeView
    /// </summary>
    public class ExportPatientItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string MRN { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public PatientClass PatientData { get; set; }
        public ObservableCollection<ExportCourseItem> Courses { get; set; }

        public string DisplayName
        {
            get { return string.Format("{0} - {1}, {2}", MRN, LastName, FirstName); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        public ExportPatientItem()
        {
            Courses = new ObservableCollection<ExportCourseItem>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Represents a course in the export TreeView
    /// </summary>
    public class ExportCourseItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string CourseName { get; set; }
        public CourseClass CourseData { get; set; }
        public ExportPatientItem Parent { get; set; }
        public ObservableCollection<ExportExaminationItem> Examinations { get; set; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        public ExportCourseItem()
        {
            Examinations = new ObservableCollection<ExportExaminationItem>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Represents an examination in the export TreeView
    /// </summary>
    public class ExportExaminationItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string ExamName { get; set; }
        public string SeriesInstanceUID { get; set; }
        public string StudyInstanceUID { get; set; }
        public string FrameOfReferenceUID { get; set; }
        public ExaminationClass ExamData { get; set; }
        public List<TreatmentPlanClass> AssociatedPlans { get; set; }
        public List<RegistrationExportInfo> AssociatedRegistrations { get; set; }
        public ExportCourseItem Parent { get; set; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        public string AssociatedPlansText
        {
            get
            {
                if (AssociatedPlans == null || AssociatedPlans.Count == 0)
                {
                    return string.Empty;
                }
                return string.Format("[Plans: {0}]", string.Join(", ", AssociatedPlans.Select(p => p.PlanName)));
            }
        }

        public string AssociatedRegistrationsText
        {
            get
            {
                if (AssociatedRegistrations == null || AssociatedRegistrations.Count == 0)
                {
                    return string.Empty;
                }
                return string.Format("[Regs: {0}]", AssociatedRegistrations.Count);
            }
        }

        public ExportExaminationItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
            AssociatedRegistrations = new List<RegistrationExportInfo>();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    #endregion

    #region Export Data Classes

    /// <summary>
    /// Represents an item to be exported via DICOM (used by DicomExportService)
    /// </summary>
    public class ExportItem
    {
        public string MRN { get; set; }
        public string PatientName { get; set; }
        public string CourseName { get; set; }
        public string ExamName { get; set; }
        public string SeriesInstanceUID { get; set; }
        public string StudyInstanceUID { get; set; }
        public string FrameOfReferenceUID { get; set; }
        public ExaminationClass ExamData { get; set; }
        public List<TreatmentPlanClass> AssociatedPlans { get; set; }
        public List<ExaminationClass> AssociatedExams { get; set; }
        public List<RegistrationExportInfo> AssociatedRegistrations { get; set; }

        public ExportItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
            AssociatedExams = new List<ExaminationClass>();
            AssociatedRegistrations = new List<RegistrationExportInfo>();
        }
    }

    /// <summary>
    /// Registration information for export
    /// </summary>
    public class RegistrationExportInfo
    {
        public string RegistrationName { get; set; }
        public string RegistrationUID { get; set; }
        public string FromFrameOfReference { get; set; }
        public string ToFrameOfReference { get; set; }
        public RegistrationClass RegistrationData { get; set; }

        // Source examination info
        public string SourceExamName { get; set; }
        public string SourceSeriesInstanceUID { get; set; }
        public string SourceStudyInstanceUID { get; set; }
        public ExaminationClass SourceExamData { get; set; }

    }

    /// <summary>
    /// Options for DICOM export operations
    /// </summary>
    public class DicomExportOptions
    {
        // Remote DICOM server settings
        public string RemoteAETitle { get; set; }
        public string RemoteIP { get; set; }
        public int RemotePort { get; set; }

        // Local settings
        public string LocalAETitle { get; set; }
        public int LocalPort { get; set; }

        // Export folder
        public string ExportFolder { get; set; }

        // Data type export flags
        public bool ExportExamination { get; set; }
        public bool ExportStructure { get; set; }
        public bool ExportPlan { get; set; }
        public bool ExportDose { get; set; }
        public bool ExportRegistrations { get; set; }

        // Registration modality filters
        public bool ExportRegistrationsCT { get; set; }
        public bool ExportRegistrationsMR { get; set; }
        public bool ExportRegistrationsPET { get; set; }
        public bool ExportRegistrationsCBCT { get; set; }
        public bool Anonymize { get; set; }
        public string AnonymizationKeyPath { get; set; }

        public DicomExportOptions()
        {
            // Default values
            RemotePort = 104;
            LocalPort = 11112;
            LocalAETitle = "DICOM_EXPORT";
            ExportExamination = true;
            ExportStructure = true;
            ExportPlan = true;
            ExportDose = true;
            Anonymize = false;
            ExportRegistrations = false;
            ExportRegistrationsCT = true;
            ExportRegistrationsMR = true;
            ExportRegistrationsPET = false;
            ExportRegistrationsCBCT = false;
        }
    }

    /// <summary>
    /// Progress information for DICOM export operations
    /// </summary>
    public class DicomExportProgress
    {
        public int PercentComplete { get; set; }
        public string StatusMessage { get; set; }
        public string DetailMessage { get; set; }
    }

    #endregion
}
using DataBaseStructure.AriaBase;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataMiningGUI
{
    #region Export Item Classes

    /// <summary>
    /// Represents a patient in the export selection tree
    /// </summary>
    public class ExportPatientItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string MRN { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }

        public string DisplayName
        {
            get { return string.Format("{0} - {1}, {2}", MRN, LastName, FirstName); }
        }

        public PatientClass PatientData { get; set; }

        public ObservableCollection<ExportCourseItem> Courses { get; set; }

        public ExportPatientItem()
        {
            Courses = new ObservableCollection<ExportCourseItem>();
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Represents a course in the export selection tree
    /// </summary>
    public class ExportCourseItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string CourseName { get; set; }
        public CourseClass CourseData { get; set; }
        public ExportPatientItem Parent { get; set; }

        public ObservableCollection<ExportExaminationItem> Examinations { get; set; }

        public ExportCourseItem()
        {
            Examinations = new ObservableCollection<ExportExaminationItem>();
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Represents an examination in the export selection tree
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
        public ExportCourseItem Parent { get; set; }

        /// <summary>
        /// Registrations where this exam is the target (ToFrameOfReference)
        /// </summary>
        public List<RegistrationExportInfo> AssociatedRegistrations { get; set; }

        public ExportExaminationItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
            AssociatedRegistrations = new List<RegistrationExportInfo>();
        }

        public string AssociatedPlansText
        {
            get
            {
                if (AssociatedPlans == null || AssociatedPlans.Count == 0)
                {
                    return string.Empty;
                }
                return string.Format("({0} plan(s))", AssociatedPlans.Count);
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
                return string.Format("[{0} reg]", AssociatedRegistrations.Count);
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Information about a registration and its associated source examination
    /// </summary>
    public class RegistrationExportInfo
    {
        /// <summary>
        /// The registration name
        /// </summary>
        public string RegistrationName { get; set; }

        /// <summary>
        /// The registration UID for DICOM export
        /// </summary>
        public string RegistrationUID { get; set; }

        /// <summary>
        /// The source examination name (the "From" in the registration)
        /// </summary>
        public string SourceExamName { get; set; }

        /// <summary>
        /// The source examination's SeriesInstanceUID for DICOM export
        /// </summary>
        public string SourceSeriesInstanceUID { get; set; }

        /// <summary>
        /// The source examination's StudyInstanceUID
        /// </summary>
        public string SourceStudyInstanceUID { get; set; }

        /// <summary>
        /// The FromFrameOfReference UID
        /// </summary>
        public string FromFrameOfReference { get; set; }

        /// <summary>
        /// The ToFrameOfReference UID (should match the plan exam's frame of reference)
        /// </summary>
        public string ToFrameOfReference { get; set; }

        /// <summary>
        /// Reference to the full examination data
        /// </summary>
        public ExaminationClass SourceExamData { get; set; }

        /// <summary>
        /// Reference to the full registration data
        /// </summary>
        public RegistrationClass RegistrationData { get; set; }
    }

    /// <summary>
    /// Flattened export item for processing by DicomExportService
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
        public List<TreatmentPlanClass> AssociatedPlans { get; set; }
        public ExaminationClass ExamData { get; set; }

        /// <summary>
        /// Registrations and their associated source images to export
        /// </summary>
        public List<RegistrationExportInfo> AssociatedRegistrations { get; set; }
        public List<ExaminationClass> AssociatedExams { get; set; }

        public ExportItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
            AssociatedRegistrations = new List<RegistrationExportInfo>();
            AssociatedExams = new List<ExaminationClass>();
        }
    }

    #endregion

    #region Export Options and Progress

    /// <summary>
    /// Options for DICOM export
    /// </summary>
    public class DicomExportOptions
    {
        public bool ExportRegistrationsCT { get; set; }
        public bool ExportRegistrationsMR { get; set; }
        public bool ExportRegistrationsPET { get; set; }
        public string ExportFolder { get; set; }
        public bool ExportExamination { get; set; }
        public bool ExportStructure { get; set; }
        public bool ExportPlan { get; set; }
        public bool ExportDose { get; set; }
        public bool ExportRegistrations { get; set; }

        public string RemoteAETitle { get; set; }
        public string RemoteIP { get; set; }
        public int RemotePort { get; set; }
        public string LocalAETitle { get; set; }
        public int LocalPort { get; set; }
    }

    /// <summary>
    /// Progress information for export operation
    /// </summary>
    public class DicomExportProgress
    {
        public int PercentComplete { get; set; }
        public string StatusMessage { get; set; }
        public string DetailMessage { get; set; }
    }

    #endregion
}

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
        public ExaminationClass ExamData { get; set; }
        public List<TreatmentPlanClass> AssociatedPlans { get; set; }
        public ExportCourseItem Parent { get; set; }

        public ExportExaminationItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
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
        public List<TreatmentPlanClass> AssociatedPlans { get; set; }
        public ExaminationClass ExamData { get; set; }

        public ExportItem()
        {
            AssociatedPlans = new List<TreatmentPlanClass>();
        }
    }

    #endregion
}

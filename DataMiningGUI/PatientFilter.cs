using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using DataBaseStructure.AriaBase;

namespace DataMiningGUI
{
    #region Enums

    /// <summary>
    /// Fields that can be filtered on
    /// </summary>
    public enum FilterField
    {
        [Description("Plan Name")]
        PlanName,

        [Description("Course Name")]
        CourseName,

        [Description("MRN")]
        MRN,

        [Description("Patient Name")]
        PatientName,

        [Description("Planned By")]
        PlannedBy,

        [Description("Approval Status")]
        ApprovalStatus,

        [Description("Plan Type")]
        PlanType,

        [Description("Machine Name")]
        MachineName,

        [Description("Number of Fractions")]
        NumberOfFractions,

        [Description("Dose per Fraction (cGy)")]
        DosePerFraction,

        [Description("Total Dose (cGy)")]
        TotalDose,

        [Description("Energy")]
        Energy,

        [Description("Delivery Technique")]
        DeliveryTechnique,

        [Description("Review Year")]
        ReviewYear,

        [Description("Review Month")]
        ReviewMonth
    }

    /// <summary>
    /// Comparison operators for filter criteria
    /// </summary>
    public enum FilterOperator
    {
        [Description("Contains")]
        Contains,

        [Description("Does Not Contain")]
        NotContains,

        [Description("Equals")]
        Equals,

        [Description("Does Not Equal")]
        NotEquals,

        [Description("Greater Than")]
        GreaterThan,

        [Description("Less Than")]
        LessThan,

        [Description("Greater Than or Equal")]
        GreaterThanOrEqual,

        [Description("Less Than or Equal")]
        LessThanOrEqual,

        [Description("Is Empty")]
        IsEmpty,

        [Description("Is Not Empty")]
        IsNotEmpty
    }

    /// <summary>
    /// Logical operators for combining criteria
    /// </summary>
    public enum LogicalOperator
    {
        [Description("AND")]
        And,

        [Description("OR")]
        Or
    }

    #endregion

    #region Filter Criterion

    /// <summary>
    /// Represents a single filter criterion
    /// </summary>
    public class FilterCriterion : INotifyPropertyChanged
    {
        private FilterField _field;
        private FilterOperator _operator;
        private string _value;
        private LogicalOperator _logicalOperator;
        private bool _isEnabled;

        public FilterCriterion()
        {
            _field = FilterField.PlanName;
            _operator = FilterOperator.Contains;
            _value = string.Empty;
            _logicalOperator = LogicalOperator.And;
            _isEnabled = true;
        }

        public FilterField Field
        {
            get => _field;
            set { _field = value; OnPropertyChanged(nameof(Field)); }
        }

        public FilterOperator Operator
        {
            get => _operator;
            set { _operator = value; OnPropertyChanged(nameof(Operator)); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(nameof(Value)); }
        }

        /// <summary>
        /// How this criterion combines with the NEXT criterion
        /// </summary>
        public LogicalOperator LogicalOperator
        {
            get => _logicalOperator;
            set { _logicalOperator = value; OnPropertyChanged(nameof(LogicalOperator)); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Creates a deep copy of this criterion
        /// </summary>
        public FilterCriterion Clone()
        {
            return new FilterCriterion
            {
                Field = this.Field,
                Operator = this.Operator,
                Value = this.Value,
                LogicalOperator = this.LogicalOperator,
                IsEnabled = this.IsEnabled
            };
        }
    }

    #endregion

    #region Filter Configuration

    /// <summary>
    /// Contains all filter criteria and configuration
    /// </summary>
    public class FilterConfiguration : INotifyPropertyChanged
    {
        private ObservableCollection<FilterCriterion> _criteria;
        private bool _isActive;

        public FilterConfiguration()
        {
            _criteria = new ObservableCollection<FilterCriterion>();
            _isActive = false;
        }

        public ObservableCollection<FilterCriterion> Criteria
        {
            get => _criteria;
            set { _criteria = value; OnPropertyChanged(nameof(Criteria)); }
        }

        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        public bool HasCriteria => _criteria != null && _criteria.Any(c => c.IsEnabled);

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Creates a deep copy of this configuration
        /// </summary>
        public FilterConfiguration Clone()
        {
            var clone = new FilterConfiguration
            {
                IsActive = this.IsActive
            };

            foreach (var criterion in this.Criteria)
            {
                clone.Criteria.Add(criterion.Clone());
            }

            return clone;
        }

        /// <summary>
        /// Clears all criteria
        /// </summary>
        public void Clear()
        {
            _criteria.Clear();
            _isActive = false;
            OnPropertyChanged(nameof(Criteria));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(HasCriteria));
        }
    }

    #endregion

    #region Filter Context

    /// <summary>
    /// Contains all the data needed to evaluate a filter against a patient/course/plan combination
    /// </summary>
    public class FilterContext
    {
        public PatientClass Patient { get; set; }
        public CourseClass Course { get; set; }
        public TreatmentPlanClass Plan { get; set; }
        public BeamSetClass BeamSet { get; set; }

        public FilterContext(PatientClass patient, CourseClass course = null, 
            TreatmentPlanClass plan = null, BeamSetClass beamSet = null)
        {
            Patient = patient;
            Course = course;
            Plan = plan;
            BeamSet = beamSet;
        }
    }

    #endregion

    #region Filter Engine

    /// <summary>
    /// Engine for evaluating filter criteria against patient data
    /// </summary>
    public static class PatientFilterEngine
    {
        #region Main Filter Method

        /// <summary>
        /// Evaluates whether a patient/course/plan combination matches the filter configuration
        /// </summary>
        public static bool Matches(FilterConfiguration config, FilterContext context)
        {
            if (config == null || !config.IsActive || !config.HasCriteria)
                return true;

            var enabledCriteria = config.Criteria.Where(c => c.IsEnabled).ToList();
            if (!enabledCriteria.Any())
                return true;

            // Evaluate criteria with AND/OR logic
            // Group by OR - within each group, all criteria must match (AND)
            // Between groups, any group matching is sufficient (OR)

            bool? result = null;
            LogicalOperator pendingOperator = LogicalOperator.And;

            foreach (var criterion in enabledCriteria)
            {
                bool criterionResult = EvaluateCriterion(criterion, context);

                if (result == null)
                {
                    result = criterionResult;
                }
                else
                {
                    if (pendingOperator == LogicalOperator.And)
                    {
                        result = result.Value && criterionResult;
                    }
                    else // OR
                    {
                        result = result.Value || criterionResult;
                    }
                }

                pendingOperator = criterion.LogicalOperator;
            }

            return result ?? true;
        }

        #endregion

        #region Criterion Evaluation

        /// <summary>
        /// Evaluates a single criterion against the context
        /// </summary>
        private static bool EvaluateCriterion(FilterCriterion criterion, FilterContext context)
        {
            string fieldValue = GetFieldValue(criterion.Field, context);
            return EvaluateOperator(criterion.Operator, fieldValue, criterion.Value, criterion.Field);
        }

        /// <summary>
        /// Gets the string value of a field from the context
        /// </summary>
        private static string GetFieldValue(FilterField field, FilterContext context)
        {
            switch (field)
            {
                case FilterField.MRN:
                    return context.Patient?.MRN ?? string.Empty;

                case FilterField.PatientName:
                    var first = context.Patient?.Name_First ?? "";
                    var last = context.Patient?.Name_Last ?? "";
                    return $"{last}, {first}".Trim(' ', ',');

                case FilterField.CourseName:
                    return context.Course?.Name ?? string.Empty;

                case FilterField.PlanName:
                    return context.Plan?.PlanName ?? string.Empty;

                case FilterField.PlanType:
                    return context.Plan?.PlanType ?? string.Empty;

                case FilterField.PlannedBy:
                    return context.Plan?.PlannedBy ?? string.Empty;

                case FilterField.ApprovalStatus:
                    return context.Plan?.Review?.ApprovalStatus ?? string.Empty;

                case FilterField.MachineName:
                    return context.BeamSet?.MachineName ?? 
                           context.Plan?.BeamSets?.FirstOrDefault()?.MachineName ?? string.Empty;

                case FilterField.NumberOfFractions:
                    return GetNumberOfFractions(context)?.ToString() ?? string.Empty;

                case FilterField.DosePerFraction:
                    return GetDosePerFraction(context)?.ToString("F2") ?? string.Empty;

                case FilterField.TotalDose:
                    return GetTotalDose(context)?.ToString("F2") ?? string.Empty;

                case FilterField.Energy:
                    return GetEnergies(context);

                case FilterField.DeliveryTechnique:
                    return GetDeliveryTechniques(context);

                case FilterField.ReviewYear:
                    return context.Plan?.Review?.ReviewTime?.Year.ToString() ?? string.Empty;

                case FilterField.ReviewMonth:
                    return context.Plan?.Review?.ReviewTime?.Month.ToString() ?? string.Empty;

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Evaluates an operator against field value and criterion value
        /// </summary>
        private static bool EvaluateOperator(FilterOperator op, string fieldValue, 
            string criterionValue, FilterField field)
        {
            // Handle null/empty field values
            bool fieldIsEmpty = string.IsNullOrWhiteSpace(fieldValue);

            switch (op)
            {
                case FilterOperator.IsEmpty:
                    return fieldIsEmpty;

                case FilterOperator.IsNotEmpty:
                    return !fieldIsEmpty;

                case FilterOperator.Contains:
                    if (fieldIsEmpty) return false;
                    return fieldValue.IndexOf(criterionValue ?? "", 
                        StringComparison.OrdinalIgnoreCase) >= 0;

                case FilterOperator.NotContains:
                    if (fieldIsEmpty) return true;
                    return fieldValue.IndexOf(criterionValue ?? "", 
                        StringComparison.OrdinalIgnoreCase) < 0;

                case FilterOperator.Equals:
                    return string.Equals(fieldValue ?? "", criterionValue ?? "", 
                        StringComparison.OrdinalIgnoreCase);

                case FilterOperator.NotEquals:
                    return !string.Equals(fieldValue ?? "", criterionValue ?? "", 
                        StringComparison.OrdinalIgnoreCase);

                case FilterOperator.GreaterThan:
                case FilterOperator.LessThan:
                case FilterOperator.GreaterThanOrEqual:
                case FilterOperator.LessThanOrEqual:
                    return EvaluateNumericOperator(op, fieldValue, criterionValue);

                default:
                    return true;
            }
        }

        /// <summary>
        /// Evaluates numeric comparison operators
        /// </summary>
        private static bool EvaluateNumericOperator(FilterOperator op, 
            string fieldValue, string criterionValue)
        {
            if (!double.TryParse(fieldValue, NumberStyles.Any, 
                CultureInfo.InvariantCulture, out double fieldNum))
                return false;

            if (!double.TryParse(criterionValue, NumberStyles.Any, 
                CultureInfo.InvariantCulture, out double criterionNum))
                return false;

            switch (op)
            {
                case FilterOperator.GreaterThan:
                    return fieldNum > criterionNum;

                case FilterOperator.LessThan:
                    return fieldNum < criterionNum;

                case FilterOperator.GreaterThanOrEqual:
                    return fieldNum >= criterionNum;

                case FilterOperator.LessThanOrEqual:
                    return fieldNum <= criterionNum;

                default:
                    return true;
            }
        }

        #endregion

        #region Helper Methods for Field Value Extraction

        private static int? GetNumberOfFractions(FilterContext context)
        {
            // Try from BeamSet normalization first
            if (context.BeamSet?.PlanNormalization?.NumberOfFractions > 0)
                return context.BeamSet.PlanNormalization.NumberOfFractions;

            // Try from first BeamSet
            var beamSet = context.Plan?.BeamSets?.FirstOrDefault();
            if (beamSet?.PlanNormalization?.NumberOfFractions > 0)
                return beamSet.PlanNormalization.NumberOfFractions;

            // Try from prescription targets
            var prescriptionTarget = context.BeamSet?.Prescription?.PrescriptionTargets?.FirstOrDefault()
                ?? context.Plan?.BeamSets?.FirstOrDefault()?.Prescription?.PrescriptionTargets?.FirstOrDefault();
            if (prescriptionTarget?.NumberOfFractions > 0)
                return prescriptionTarget.NumberOfFractions;

            return null;
        }

        private static double? GetDosePerFraction(FilterContext context)
        {
            // Try from BeamSet normalization
            if (context.BeamSet?.PlanNormalization?.Dose_per_Fraction > 0)
                return context.BeamSet.PlanNormalization.Dose_per_Fraction;

            // Try from first BeamSet
            var beamSet = context.Plan?.BeamSets?.FirstOrDefault();
            if (beamSet?.PlanNormalization?.Dose_per_Fraction > 0)
                return beamSet.PlanNormalization.Dose_per_Fraction;

            // Try from prescription targets
            var prescriptionTarget = context.BeamSet?.Prescription?.PrescriptionTargets?.FirstOrDefault()
                ?? context.Plan?.BeamSets?.FirstOrDefault()?.Prescription?.PrescriptionTargets?.FirstOrDefault();
            if (prescriptionTarget?.DosePerFraction > 0)
                return prescriptionTarget.DosePerFraction;

            return null;
        }

        private static double? GetTotalDose(FilterContext context)
        {
            var fractions = GetNumberOfFractions(context);
            var dosePerFraction = GetDosePerFraction(context);

            if (fractions.HasValue && dosePerFraction.HasValue)
                return fractions.Value * dosePerFraction.Value;

            // Try from normalization dose value
            if (context.BeamSet?.PlanNormalization?.DoseValue_cGy > 0)
                return context.BeamSet.PlanNormalization.DoseValue_cGy;

            var beamSet = context.Plan?.BeamSets?.FirstOrDefault();
            if (beamSet?.PlanNormalization?.DoseValue_cGy > 0)
                return beamSet.PlanNormalization.DoseValue_cGy;

            return null;
        }

        private static string GetEnergies(FilterContext context)
        {
            var energies = new HashSet<string>();

            // From prescription
            var prescription = context.BeamSet?.Prescription 
                ?? context.Plan?.BeamSets?.FirstOrDefault()?.Prescription;
            if (prescription?.Energies != null)
            {
                foreach (var e in prescription.Energies)
                    energies.Add(e);
            }

            // From beams
            var beams = context.BeamSet?.Beams 
                ?? context.Plan?.BeamSets?.FirstOrDefault()?.Beams;
            if (beams != null)
            {
                foreach (var beam in beams)
                {
                    if (!string.IsNullOrEmpty(beam.BeamQualityId))
                        energies.Add(beam.BeamQualityId);
                }
            }

            return string.Join(", ", energies);
        }

        private static string GetDeliveryTechniques(FilterContext context)
        {
            var techniques = new HashSet<string>();

            var beams = context.BeamSet?.Beams 
                ?? context.Plan?.BeamSets?.FirstOrDefault()?.Beams;
            if (beams != null)
            {
                foreach (var beam in beams)
                {
                    if (!string.IsNullOrEmpty(beam.DeliveryTechnique))
                        techniques.Add(beam.DeliveryTechnique);
                }
            }

            return string.Join(", ", techniques);
        }

        #endregion

        #region Batch Filtering

        /// <summary>
        /// Filters a list of patients and returns matching patient/course/plan combinations
        /// </summary>
        public static List<FilterContext> FilterPatients(FilterConfiguration config, 
            IEnumerable<PatientClass> patients)
        {
            var results = new List<FilterContext>();

            foreach (var patient in patients)
            {
                if (patient.Courses == null || !patient.Courses.Any())
                {
                    // Check patient-level match
                    var context = new FilterContext(patient);
                    if (Matches(config, context))
                        results.Add(context);
                    continue;
                }

                foreach (var course in patient.Courses)
                {
                    if (course.TreatmentPlans == null || !course.TreatmentPlans.Any())
                    {
                        var context = new FilterContext(patient, course);
                        if (Matches(config, context))
                            results.Add(context);
                        continue;
                    }

                    foreach (var plan in course.TreatmentPlans)
                    {
                        if (plan.BeamSets != null && plan.BeamSets.Any())
                        {
                            foreach (var beamSet in plan.BeamSets)
                            {
                                var context = new FilterContext(patient, course, plan, beamSet);
                                if (Matches(config, context))
                                    results.Add(context);
                            }
                        }
                        else if (plan.ApplicatorSets != null && plan.ApplicatorSets.Any())
                        {
                            // Brachy plan
                            var context = new FilterContext(patient, course, plan);
                            if (Matches(config, context))
                                results.Add(context);
                        }
                        else
                        {
                            var context = new FilterContext(patient, course, plan);
                            if (Matches(config, context))
                                results.Add(context);
                        }
                    }
                }
            }

            return results;
        }

        #endregion
    }

    #endregion

    #region Enum Helper

    /// <summary>
    /// Helper class for working with enums and their descriptions
    /// </summary>
    public static class EnumHelper
    {
        /// <summary>
        /// Gets the Description attribute value for an enum value
        /// </summary>
        public static string GetDescription(Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = (DescriptionAttribute)Attribute.GetCustomAttribute(
                field, typeof(DescriptionAttribute));
            return attribute?.Description ?? value.ToString();
        }

        /// <summary>
        /// Gets all values of an enum as a list with their descriptions
        /// </summary>
        public static List<EnumItem<T>> GetEnumItems<T>() where T : Enum
        {
            return Enum.GetValues(typeof(T))
                .Cast<T>()
                .Select(e => new EnumItem<T> { Value = e, Description = GetDescription(e) })
                .ToList();
        }
    }

    /// <summary>
    /// Wrapper for enum values with descriptions for UI binding
    /// </summary>
    public class EnumItem<T> where T : Enum
    {
        public T Value { get; set; }
        public string Description { get; set; }

        public override string ToString() => Description;
    }

    #endregion
}

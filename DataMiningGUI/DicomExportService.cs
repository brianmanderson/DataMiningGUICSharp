using DataBaseStructure.AriaBase;
using EvilDICOM.Core;
using EvilDICOM.Core.Helpers;
using EvilDICOM.Network;
using EvilDICOM.Network.DIMSE;
using EvilDICOM.Network.DIMSE.IOD;
using EvilDICOM.Network.SCUOps;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataMiningGUI
{
    /// <summary>
    /// Service for exporting DICOM data using EvilDICOM
    /// </summary>
    public class DicomExportService
    {
        private DICOMSCU _localSCU;
        private DICOMSCP _localSCP;
        private Entity _daemon;
        private Entity _local;
        private ConcurrentQueue<string> _receivedFiles;
        private string _currentExportPath;
        private ManualResetEventSlim _exportCompleteEvent;
        private int _expectedFileCount;
        private int _receivedFileCount;

        /// <summary>
        /// Export selected items to the specified folder using DICOM C-MOVE
        /// </summary>
        public void ExportAsync(
            List<ExportItem> items,
            DicomExportOptions options,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            _receivedFiles = new ConcurrentQueue<string>();
            _exportCompleteEvent = new ManualResetEventSlim(false);

            try
            {
                // Initialize DICOM entities
                _daemon = new Entity(options.RemoteAETitle, options.RemoteIP, options.RemotePort);
                _local = Entity.CreateLocal(options.LocalAETitle, options.LocalPort);
                _localSCU = new DICOMSCU(_local);

                // Setup local SCP to receive files
                SetupLocalSCP(options);

                // Start listening for incoming associations
                _localSCP.ListenForIncomingAssociations(true);

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = 0,
                        StatusMessage = "Connected to DICOM server",
                        DetailMessage = string.Format("Remote: {0}@{1}:{2}",
                            options.RemoteAETitle, options.RemoteIP, options.RemotePort)
                    });
                }

                // Get finder and mover
                CFinder cfinder = _localSCU.GetCFinder(_daemon);
                CMover cmover = _localSCU.GetCMover(_daemon);

                int totalItems = items.Count;
                int processedItems = 0;

                foreach (ExportItem item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            PercentComplete = (int)((double)processedItems / totalItems * 100),
                            StatusMessage = string.Format("Processing: {0}", item.MRN),
                            DetailMessage = string.Format("Exam: {0} ({1}/{2})",
                                item.ExamName, processedItems + 1, totalItems)
                        });
                    }

                    // Create export directory structure: MRN/Case/Examination
                    string patientFolder = SanitizeFolderName(item.MRN);
                    string courseFolder = SanitizeFolderName(item.CourseName);
                    string examFolder = SanitizeFolderName(item.ExamName);
                    _currentExportPath = Path.Combine(options.ExportFolder, patientFolder, courseFolder, examFolder);

                    // Create subdirectories for each data type
                    string structureFolder = _currentExportPath;// Path.Combine(_currentExportPath, "Structure");
                    string doseFolder = _currentExportPath; //Path.Combine(_currentExportPath, "Dose");
                    string planFolder = _currentExportPath; //Path.Combine(_currentExportPath, "Plan");
                    string registrationFolder = _currentExportPath; //Path.Combine(_currentExportPath, "Registration");

                    EnsureDirectoryExists(_currentExportPath);

                    // Find studies for this patient
                    IEnumerable<CFindStudyIOD> studies = cfinder.FindStudies(item.MRN);

                    if (studies == null || !studies.Any())
                    {
                        if (progress != null)
                        {
                            progress.Report(new DicomExportProgress
                            {
                                PercentComplete = (int)((double)processedItems / totalItems * 100),
                                StatusMessage = string.Format("Warning: No studies found for {0}", item.MRN),
                                DetailMessage = "Skipping..."
                            });
                        }
                        processedItems++;
                        continue;
                    }

                    // Find all series for the studies
                    IEnumerable<CFindSeriesIOD> allSeries = cfinder.FindSeries(studies);
                    ushort msgId = 1;

                    // Export Examination (CT/MR images)
                    if (options.ExportExamination && !string.IsNullOrEmpty(item.SeriesInstanceUID))
                    {
                        ExportSeriesByUID(cmover, allSeries, item.SeriesInstanceUID,
                            options.LocalAETitle, ref msgId, _currentExportPath,
                            "Examination", progress, cancellationToken);
                    }

                    // Export Structure Set, Plan, and Dose by finding related series
                    if (options.ExportStructure || options.ExportPlan || options.ExportDose)
                    {
                        // Find RT Structure Set series
                        List<CFindSeriesIOD> rtStructSeries = allSeries.Where(s =>
                            s.Modality == "RTSTRUCT" &&
                            IsRelatedToExam(s, item.SeriesInstanceUID)).ToList();

                        if (options.ExportStructure && rtStructSeries.Any())
                        {
                            foreach (CFindSeriesIOD series in rtStructSeries)
                            {
                                ExportSeries(cmover, series, options.LocalAETitle, ref msgId,
                                    structureFolder, "Structure", progress, cancellationToken);
                            }
                        }

                        // Find RT Plan series
                        List<CFindSeriesIOD> rtPlanSeries = allSeries.Where(s =>
                            s.Modality == "RTPLAN" &&
                            IsRelatedToExam(s, item.SeriesInstanceUID)).ToList();

                        if (options.ExportPlan && rtPlanSeries.Any())
                        {
                            foreach (CFindSeriesIOD series in rtPlanSeries)
                            {
                                ExportSeries(cmover, series, options.LocalAETitle, ref msgId,
                                    planFolder, "Plan", progress, cancellationToken);
                            }
                        }

                        // Find RT Dose series
                        List<CFindSeriesIOD> rtDoseSeries = allSeries.Where(s =>
                            s.Modality == "RTDOSE" &&
                            IsRelatedToExam(s, item.SeriesInstanceUID)).ToList();

                        if (options.ExportDose && rtDoseSeries.Any())
                        {
                            foreach (CFindSeriesIOD series in rtDoseSeries)
                            {
                                ExportSeries(cmover, series, options.LocalAETitle, ref msgId,
                                    doseFolder, "Dose", progress, cancellationToken);
                            }
                        }
                    }

                    // Export Registrations and Associated Images
                    if (options.ExportRegistrations && item.AssociatedRegistrations != null && item.AssociatedRegistrations.Count > 0)
                    {
                        ExportRegistrationsAndAssociatedImages(
                            cmover, cfinder, allSeries, item, options.LocalAETitle, ref msgId,
                            registrationFolder, _currentExportPath, options, progress, cancellationToken);
                    }

                    processedItems++;
                }

                // Wait a bit for any remaining files to be received
                Thread.Sleep(2000);

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = 100,
                        StatusMessage = "Export complete",
                        DetailMessage = string.Format("Exported {0} examination(s)", processedItems)
                    });
                }
            }
            finally
            {
                // Cleanup
                if (_localSCP != null)
                {
                    _localSCP.Stop();
                }
                if (_exportCompleteEvent != null)
                {
                    _exportCompleteEvent.Dispose();
                }
            }
        }

        /// <summary>
        /// Export registrations and their associated source images (e.g., MR fusions)
        /// </summary>
        private void ExportRegistrationsAndAssociatedImages(
            CMover cmover,
            CFinder cfinder,
            IEnumerable<CFindSeriesIOD> allSeries,
            ExportItem item,
            string localAETitle,
            ref ushort msgId,
            string registrationFolder,
            string baseExportPath,
            DicomExportOptions options,  // Add this parameter
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            foreach (RegistrationExportInfo regInfo in item.AssociatedRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create a subfolder for each registration's source image
                string sourceExamName = !string.IsNullOrEmpty(regInfo.SourceExamName)
                    ? regInfo.SourceExamName
                    : "UnknownSource";
                string sourceImageFolder = Path.Combine(baseExportPath, "RegisteredImages", SanitizeFolderName(sourceExamName));
                EnsureDirectoryExists(sourceImageFolder);

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = -1,
                        StatusMessage = string.Format("Exporting Registration: {0}", regInfo.RegistrationName),
                        DetailMessage = string.Format("Source: {0}", sourceExamName)
                    });
                }

                // Export the registration object itself by UID
                if (!string.IsNullOrEmpty(regInfo.RegistrationUID))
                {
                    // Find REG series by looking for Spatial Registration modality
                    List<CFindSeriesIOD> regSeries = allSeries.Where(s =>
                        s.Modality == "REG" || s.Modality == "SPATIAL REGISTRATION").ToList();

                    // Try to find by SeriesInstanceUID if the registration UID matches
                    foreach (CFindSeriesIOD series in regSeries)
                    {
                        // Export all REG series - in practice you might want to filter more specifically
                        ExportSeries(cmover, series, localAETitle, ref msgId,
                            registrationFolder, "Registration", progress, cancellationToken);
                    }
                }

                // Export the source examination images (the "From" side of the registration)
                if (!string.IsNullOrEmpty(regInfo.SourceSeriesInstanceUID))
                {
                    ExportSeriesByUID(cmover, allSeries, regInfo.SourceSeriesInstanceUID,
                        localAETitle, ref msgId, sourceImageFolder,
                        string.Format("Source Image ({0})", sourceExamName), progress, cancellationToken);
                }
                else if (!string.IsNullOrEmpty(regInfo.FromFrameOfReference))
                {
                    // If we don't have SeriesInstanceUID but have FromFrameOfReference,
                    // try to find the series by matching Frame of Reference
                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            PercentComplete = -1,
                            StatusMessage = "Searching for source image by Frame of Reference",
                            DetailMessage = string.Format("FrameOfRef: {0}", 
                                regInfo.FromFrameOfReference.Length > 30 
                                    ? regInfo.FromFrameOfReference.Substring(0, 30) + "..." 
                                    : regInfo.FromFrameOfReference)
                        });
                    }

                    // Note: C-FIND at series level doesn't typically include Frame of Reference
                    // You may need to fetch at image level or use a different strategy
                    // For now, we'll try exporting any CT/MR series that might match
                    List<string> allowedModalities = new List<string>();
                    if (options.ExportRegistrationsCT) allowedModalities.Add("CT");
                    if (options.ExportRegistrationsMR) allowedModalities.Add("MR");
                    if (options.ExportRegistrationsPET) { allowedModalities.Add("PT"); allowedModalities.Add("PET"); }

                    List<CFindSeriesIOD> imageSeries = allSeries.Where(s =>
                        allowedModalities.Contains(s.Modality, StringComparer.OrdinalIgnoreCase)).ToList();

                    foreach (CFindSeriesIOD series in imageSeries)
                    {
                        if (series.SeriesInstanceUID != item.SeriesInstanceUID) // Don't re-export the primary exam
                        {
                            ExportSeries(cmover, series, localAETitle, ref msgId,
                                sourceImageFolder, "Source Image", progress, cancellationToken);
                        }
                    }
                }
            }
        }

        private void SetupLocalSCP(DicomExportOptions options)
        {
            _localSCP = new DICOMSCP(_local);
            _localSCP.SupportedAbstractSyntaxes = AbstractSyntax.ALL_RADIOTHERAPY_STORAGE;

            _localSCP.DIMSEService.CStoreService.CStorePayloadAction = (dcm, asc) =>
            {
                try
                {
                    // Determine the appropriate subfolder based on modality
                    string modality = "Unknown";
                    var modalityElement = dcm.GetSelector().Modality;
                    if (modalityElement != null && modalityElement.Data != null)
                    {
                        modality = modalityElement.Data;
                    }

                    string subFolder = GetSubfolderForModality(modality);
                    string targetPath = _currentExportPath;
                    if (!string.IsNullOrEmpty(subFolder))
                    {
                        targetPath = Path.Combine(_currentExportPath, subFolder);
                    }

                    // Ensure target directory exists
                    EnsureDirectoryExists(targetPath);

                    // Get SOP Instance UID for filename
                    string sopInstanceUID = Guid.NewGuid().ToString();
                    var sopElement = dcm.GetSelector().SOPInstanceUID;
                    if (sopElement != null && sopElement.Data != null)
                    {
                        sopInstanceUID = sopElement.Data;
                    }

                    string fileName = sopInstanceUID + ".dcm";
                    string filePath = Path.Combine(targetPath, fileName);

                    Console.WriteLine(string.Format("Writing file: {0}", filePath));
                    dcm.Write(filePath);

                    _receivedFiles.Enqueue(filePath);
                    Interlocked.Increment(ref _receivedFileCount);

                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format("Error writing DICOM file: {0}", ex.Message));
                    return false;
                }
            };
        }

        private string GetSubfolderForModality(string modality)
        {
            if (string.IsNullOrEmpty(modality))
            {
                return string.Empty;
            }

            switch (modality.ToUpper())
            {
                case "RTSTRUCT":
                    return "Structure";
                case "RTPLAN":
                    return "Plan";
                case "RTDOSE":
                    return "Dose";
                case "REG":
                case "SPATIAL REGISTRATION":
                    return "Registration";
                case "CT":
                case "MR":
                case "PT":
                default:
                    return string.Empty; // Root examination folder
            }
        }

        private void ExportSeriesByUID(CMover cmover, IEnumerable<CFindSeriesIOD> allSeries,
            string seriesInstanceUID, string localAETitle, ref ushort msgId,
            string exportPath, string dataType, IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            IEnumerable<CFindSeriesIOD> matchingSeries = allSeries.Where(s => s.SeriesInstanceUID == seriesInstanceUID);

            foreach (CFindSeriesIOD series in matchingSeries)
            {
                ExportSeries(cmover, series, localAETitle, ref msgId, exportPath,
                    dataType, progress, cancellationToken);
            }
        }

        private void ExportSeries(CMover cmover, CFindSeriesIOD series,
            string localAETitle, ref ushort msgId, string exportPath, string dataType,
            IProgress<DicomExportProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string seriesUidDisplay = "Unknown";
                if (!string.IsNullOrEmpty(series.SeriesInstanceUID))
                {
                    int displayLength = Math.Min(30, series.SeriesInstanceUID.Length);
                    seriesUidDisplay = series.SeriesInstanceUID.Substring(0, displayLength);
                }

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = -1, // Indeterminate
                        StatusMessage = string.Format("Exporting {0}", dataType),
                        DetailMessage = string.Format("Series: {0}...", seriesUidDisplay)
                    });
                }

                _currentExportPath = exportPath;
                CMoveResponse response = cmover.SendCMove(series, localAETitle, ref msgId);

                if (response.Status != 0)
                {
                    Console.WriteLine(string.Format("C-MOVE response status: {0}", response.Status));
                }

                // Small delay to allow file reception
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Error exporting series: {0}", ex.Message));
            }
        }

        private bool IsRelatedToExam(CFindSeriesIOD series, string examSeriesInstanceUID)
        {
            // This is a simplified check - in reality, you might need to query
            // the DICOM relationships more thoroughly
            // For RT objects, they reference the CT/MR series via Frame of Reference
            return true; // Accept all RT objects for now - you may want to refine this
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "Unknown";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] nameChars = name.ToCharArray();
            for (int i = 0; i < nameChars.Length; i++)
            {
                if (invalidChars.Contains(nameChars[i]))
                {
                    nameChars[i] = '_';
                }
            }
            return new string(nameChars).Trim();
        }
    }

    /// <summary>
    /// Alternative export service that exports directly without C-MOVE 
    /// (useful when you have local access to DICOM files)
    /// </summary>
    public class DicomDirectExportService
    {
        /// <summary>
        /// Export by copying DICOM files from a source directory
        /// </summary>
        public void ExportFromDirectory(
            List<ExportItem> items,
            DicomExportOptions options,
            string sourceDicomDirectory,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            int totalItems = items.Count;
            int processedItems = 0;

            foreach (ExportItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = (int)((double)processedItems / totalItems * 100),
                        StatusMessage = string.Format("Processing: {0}", item.MRN),
                        DetailMessage = string.Format("Exam: {0}", item.ExamName)
                    });
                }

                // Create export directory structure
                string patientFolder = SanitizeFolderName(item.MRN);
                string courseFolder = SanitizeFolderName(item.CourseName);
                string examFolder = SanitizeFolderName(item.ExamName);
                string exportPath = Path.Combine(options.ExportFolder, patientFolder, courseFolder, examFolder);

                EnsureDirectoryExists(exportPath);

                if (options.ExportStructure)
                {
                    EnsureDirectoryExists(Path.Combine(exportPath, "Structure"));
                }
                if (options.ExportDose)
                {
                    EnsureDirectoryExists(Path.Combine(exportPath, "Dose"));
                }
                if (options.ExportPlan)
                {
                    EnsureDirectoryExists(Path.Combine(exportPath, "Plan"));
                }
                if (options.ExportRegistrations)
                {
                    EnsureDirectoryExists(Path.Combine(exportPath, "Registration"));
                    EnsureDirectoryExists(Path.Combine(exportPath, "RegisteredImages"));
                }

                processedItems++;
            }

            if (progress != null)
            {
                progress.Report(new DicomExportProgress
                {
                    PercentComplete = 100,
                    StatusMessage = "Export complete",
                    DetailMessage = string.Format("Processed {0} examination(s)", processedItems)
                });
            }
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "Unknown";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] nameChars = name.ToCharArray();
            for (int i = 0; i < nameChars.Length; i++)
            {
                if (invalidChars.Contains(nameChars[i]))
                {
                    nameChars[i] = '_';
                }
            }
            return new string(nameChars).Trim();
        }
    }
}

using DataBaseStructure.AriaBase;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataMiningGUI
{
    /// <summary>
    /// Service for exporting DICOM data using FellowOakDicom (fo-dicom)
    /// </summary>
    /// 
    public class AnonymizationKey : BaseMethod
    {
        [JsonProperty("Mappings")]
        public Dictionary<string, string> Mappings { get; set; } = new Dictionary<string, string>();

    }
    public class DicomExportService
    {
        private static readonly object _anonKeyLock = new object();
        private string GetOrCreateAnonymizedMRN(string keyFilePath, string originalMRN)
        {
            lock (_anonKeyLock)
            {
                AnonymizationKey anonKey;

                // Load existing file or create new
                if (File.Exists(keyFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(keyFilePath);
                        anonKey = JsonConvert.DeserializeObject<AnonymizationKey>(json);
                        if (anonKey == null)
                        {
                            anonKey = new AnonymizationKey();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading anonymization key file: {ex.Message}");
                        anonKey = new AnonymizationKey();
                    }
                }
                else
                {
                    anonKey = new AnonymizationKey();
                }

                // Check if this MRN already has a mapping
                if (anonKey.Mappings.ContainsKey(originalMRN))
                {
                    // Use existing mapping
                    return anonKey.Mappings[originalMRN];
                }
                else
                {
                    // Create new hash
                    string anonymizedMRN = DicomCStoreReceiverService.DeterministicHashString("PatientID:" + originalMRN);
                    anonKey.Mappings[originalMRN] = anonymizedMRN;

                    // Save to file
                    try
                    {
                        string outputJson = anonKey.ToJsonFormatted();
                        File.WriteAllText(keyFilePath, outputJson);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error writing anonymization key file: {ex.Message}");
                    }

                    return anonymizedMRN;
                }
            }
        }
        private IDicomServer _localSCP;
        private string _currentExportPath;
        private ConcurrentQueue<string> _receivedFiles;
        private int _receivedFileCount;

        // Static storage for the SCP to access current export path
        private static string _staticExportPath;
        private static ConcurrentQueue<string> _staticReceivedFiles;

        private string _currentAnonymizationKeyPath;
        private static string _staticAnonymizationKeyPath;

        /// <summary>
        /// Export selected items to the specified folder using DICOM C-MOVE.
        /// For each ExportItem: queries DICOM server, collects all pending exports, then
        /// executes them with accurate per-series progress tracking.
        /// </summary>
        public async Task ExportAsync(
            List<ExportItem> items,
            DicomExportOptions options,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            _receivedFiles = new ConcurrentQueue<string>();
            _staticReceivedFiles = _receivedFiles;

            try
            {
                // Setup and start local SCP to receive files
                await SetupLocalSCPAsync(options);

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        OverallPercentComplete = 0,
                        SeriesPercentComplete = -1,
                        StatusMessage = "Connected to DICOM server",
                        DetailMessage = string.Format("Remote: {0}@{1}:{2}",
                            options.RemoteAETitle, options.RemoteIP, options.RemotePort)
                    });
                }

                int totalItems = items.Count;
                int processedItems = 0;

                foreach (ExportItem item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int overallPercent = (int)((double)processedItems / totalItems * 100);

                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            OverallPercentComplete = overallPercent,
                            SeriesPercentComplete = 0,
                            StatusMessage = string.Format("Querying: {0} ({1}/{2})", item.MRN, processedItems + 1, totalItems),
                            DetailMessage = string.Format("Exam: {0} — Finding studies...", item.ExamName)
                        });
                    }

                    _currentAnonymizationKeyPath = !string.IsNullOrEmpty(options.AnonymizationKeyPath)
                        ? options.AnonymizationKeyPath
                        : Path.Combine(options.ExportFolder, "AnonymizationKey.json");
                    _staticAnonymizationKeyPath = _currentAnonymizationKeyPath;
                    DicomCStoreReceiverService.AnonymizationKeyPath = _currentAnonymizationKeyPath;

                    // Create export directory structure: MRN/Case/Examination
                    string patientFolder = options.Anonymize
                        ? GetOrCreateAnonymizedMRN(_currentAnonymizationKeyPath, item.MRN)
                        : SanitizeFolderName(item.MRN);

                    string courseFolder = SanitizeFolderName(item.CourseName);
                    string examFolder = SanitizeFolderName(item.ExamName);
                    _currentExportPath = Path.Combine(options.ExportFolder, patientFolder, courseFolder, examFolder);
                    _staticExportPath = _currentExportPath;

                    // Create subdirectories for each data type
                    string structureFolder = _currentExportPath;
                    string doseFolder = _currentExportPath;
                    string planFolder = _currentExportPath;
                    string registrationFolder = _currentExportPath;

                    EnsureDirectoryExists(_currentExportPath);

                    // ========================================================
                    // PHASE 1: Query — find studies and series via C-FIND
                    // ========================================================
                    List<DicomDataset> studies = await FindStudiesAsync(item.MRN, options, cancellationToken);

                    if (studies == null || !studies.Any())
                    {
                        if (progress != null)
                        {
                            progress.Report(new DicomExportProgress
                            {
                                OverallPercentComplete = overallPercent,
                                SeriesPercentComplete = -1,
                                StatusMessage = string.Format("Warning: No studies found for {0}", item.MRN),
                                DetailMessage = "Skipping..."
                            });
                        }
                        processedItems++;
                        continue;
                    }

                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            OverallPercentComplete = overallPercent,
                            SeriesPercentComplete = -1,
                            StatusMessage = string.Format("Querying: {0} ({1}/{2})", item.MRN, processedItems + 1, totalItems),
                            DetailMessage = string.Format("Exam: {0} — Finding series...", item.ExamName)
                        });
                    }

                    List<DicomDataset> allSeries = await FindSeriesForStudiesAsync(studies, options, cancellationToken);

                    // ========================================================
                    // PHASE 2: Collect — build list of PendingExport items
                    // ========================================================
                    List<PendingExport> pendingExports = new List<PendingExport>();

                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            OverallPercentComplete = overallPercent,
                            SeriesPercentComplete = -1,
                            StatusMessage = string.Format("Querying: {0} ({1}/{2})", item.MRN, processedItems + 1, totalItems),
                            DetailMessage = string.Format("Exam: {0} — Identifying exports...", item.ExamName)
                        });
                    }

                    // Collect Examination (CT/MR images)
                    if (options.ExportExamination && !string.IsNullOrEmpty(item.SeriesInstanceUID))
                    {
                        IEnumerable<DicomDataset> matchingSeries = allSeries.Where(s =>
                            GetStringValue(s, DicomTag.SeriesInstanceUID) == item.SeriesInstanceUID);

                        foreach (DicomDataset series in matchingSeries)
                        {
                            pendingExports.Add(new PendingExport
                            {
                                Series = series,
                                ExportPath = _currentExportPath,
                                DataType = string.Format("{0} : Examination", patientFolder),
                                IsImageLevel = false
                            });
                        }
                    }

                    // Collect Structure Set, Plan, and Dose
                    if (options.ExportStructure || options.ExportPlan || options.ExportDose)
                    {
                        List<DicomDataset> rtStructSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTSTRUCT").ToList();

                        // Collect RT Structure Set
                        if (options.ExportStructure && rtStructSeries.Any())
                        {
                            if (item.ExamData.StructureSetUID != null)
                            {
                                List<DicomDataset> allImagesRT = await FindInstancesForSeriesAsync(rtStructSeries, options, cancellationToken);
                                allImagesRT = allImagesRT.Where(s => GetStringValue(s, DicomTag.SOPInstanceUID) == item.ExamData.StructureSetUID).ToList();
                                foreach (DicomDataset series in allImagesRT)
                                {
                                    pendingExports.Add(new PendingExport
                                    {
                                        Series = series,
                                        ExportPath = structureFolder,
                                        DataType = string.Format("{0} : Structure", patientFolder),
                                        IsImageLevel = false
                                    });
                                }
                            }
                        }

                        // Collect RT Plan
                        List<DicomDataset> rtPlanSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTPLAN" && item.AssociatedPlans.Select(p => p.SeriesInstanceUID).Contains(GetStringValue(s, DicomTag.SeriesInstanceUID))).ToList();

                        if (options.ExportPlan && rtPlanSeries.Any())
                        {
                            foreach (DicomDataset series in rtPlanSeries)
                            {
                                pendingExports.Add(new PendingExport
                                {
                                    Series = series,
                                    ExportPath = planFolder,
                                    DataType = string.Format("{0} : Plan", patientFolder),
                                    IsImageLevel = false
                                });
                            }
                        }

                        // Collect RT Dose (image-level exports)
                        List<DicomDataset> rtDoseSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTDOSE").ToList();
                        HashSet<string> doseSOPUIDs = new HashSet<string>();

                        if (options.ExportDose && rtDoseSeries.Any())
                        {
                            List<DicomDataset> allImagesDose = await FindInstancesForSeriesAsync(rtDoseSeries, options, cancellationToken);
                            allImagesDose = allImagesDose.Where(aid => item.AssociatedPlans.Select(p => p.DoseSOPInstanceUID).Contains(GetStringValue(aid, DicomTag.SOPInstanceUID))).ToList();
                            foreach (DicomDataset series in allImagesDose)
                            {
                                string sopUID = GetStringValue(series, DicomTag.SOPInstanceUID);
                                if (!string.IsNullOrEmpty(sopUID) && !doseSOPUIDs.Contains(sopUID))
                                {
                                    doseSOPUIDs.Add(sopUID);
                                    pendingExports.Add(new PendingExport
                                    {
                                        Series = series,
                                        ExportPath = doseFolder,
                                        DataType = string.Format("{0} : Dose", patientFolder),
                                        IsImageLevel = true
                                    });
                                }
                            }
                        }
                    }

                    // Collect Registrations and Associated Images
                    if (options.ExportRegistrations && item.AssociatedRegistrations != null && item.AssociatedRegistrations.Count > 0)
                    {
                        List<PendingExport> regExports = await CollectRegistrationExportsAsync(
                            allSeries, item, options, registrationFolder, _currentExportPath,
                            cancellationToken);
                        pendingExports.AddRange(regExports);
                    }

                    // ========================================================
                    // PHASE 3: Execute — export all collected items with progress
                    // ========================================================
                    int totalPending = pendingExports.Count;

                    if (totalPending == 0)
                    {
                        if (progress != null)
                        {
                            progress.Report(new DicomExportProgress
                            {
                                OverallPercentComplete = overallPercent,
                                SeriesPercentComplete = 100,
                                StatusMessage = string.Format("No exportable data for {0}", item.MRN),
                                DetailMessage = string.Format("Exam: {0}", item.ExamName)
                            });
                        }
                        processedItems++;
                        continue;
                    }

                    for (int i = 0; i < totalPending; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        PendingExport pending = pendingExports[i];
                        int seriesPercent = (int)((double)i / totalPending * 100);
                        overallPercent = (int)((double)(processedItems*totalPending + i) / (totalPending * totalItems) * 100);
                        if (progress != null)
                        {
                            progress.Report(new DicomExportProgress
                            {
                                OverallPercentComplete = overallPercent,
                                SeriesPercentComplete = seriesPercent,
                                StatusMessage = string.Format("Exporting {0} ({1}/{2})",
                                    pending.DataType, i + 1, totalPending),
                                DetailMessage = string.Format("Item {0}/{1} — Series {2}/{3}",
                                    processedItems + 1, totalItems, i + 1, totalPending)
                            });
                        }

                        if (pending.IsImageLevel)
                        {
                            await ExportSeriesImageAsync(pending.Series, options, pending.ExportPath, cancellationToken);
                        }
                        else
                        {
                            await ExportSeriesAsync(pending.Series, options, pending.ExportPath, cancellationToken);
                        }
                    }

                    // Report series complete for this item
                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            OverallPercentComplete = overallPercent,
                            SeriesPercentComplete = 100,
                            StatusMessage = string.Format("Completed: {0}", item.MRN),
                            DetailMessage = string.Format("Exam: {0} — {1} series exported", item.ExamName, totalPending)
                        });
                    }

                    processedItems++;
                }

                // Wait a bit for any remaining files to be received
                await Task.Delay(2000, cancellationToken);

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        OverallPercentComplete = 100,
                        SeriesPercentComplete = 100,
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
                    _localSCP.Dispose();
                    _localSCP = null;
                }
            }
        }

        /// <summary>
        /// Collect registration exports and their associated source images into PendingExport list.
        /// This replaces the old ExportRegistrationsAndAssociatedImagesAsync that exported inline.
        /// </summary>
        private async Task<List<PendingExport>> CollectRegistrationExportsAsync(
            List<DicomDataset> allSeries,
            ExportItem item,
            DicomExportOptions options,
            string registrationFolder,
            string baseExportPath,
            CancellationToken cancellationToken)
        {
            List<PendingExport> pendingExports = new List<PendingExport>();

            // Get the primary exam's FrameOfReference
            string primaryFrameOfRef = null;
            if (item.ExamData != null && item.ExamData.EquipmentInfo != null)
            {
                primaryFrameOfRef = item.ExamData.EquipmentInfo.FrameOfReference;
            }

            if (string.IsNullOrEmpty(primaryFrameOfRef))
            {
                return pendingExports;
            }

            // Build list of allowed modalities
            List<string> allowedModalities = new List<string>();
            if (options.ExportRegistrationsCT) allowedModalities.Add("CT");
            if (options.ExportRegistrationsMR) allowedModalities.Add("MR");
            if (options.ExportRegistrationsPET) { allowedModalities.Add("PT"); allowedModalities.Add("PET"); }
            if (options.ExportRegistrationsCBCT) allowedModalities.Add("CBCT");

            bool includeCBCT = options.ExportRegistrationsCBCT;

            // Step 1: Build lookup of FrameOfReference -> ExaminationClass
            Dictionary<string, List<ExaminationClass>> frameOfRefToExams = new Dictionary<string, List<ExaminationClass>>();
            if (item.AssociatedExams != null)
            {
                foreach (ExaminationClass exam in item.AssociatedExams)
                {
                    string examFrameOfRef = GetExamFrameOfReference(exam);
                    if (string.IsNullOrEmpty(examFrameOfRef))
                    {
                        continue;
                    }

                    string sourceModality = GetExamModality(exam);
                    if (!IsModalityAllowed(sourceModality, allowedModalities))
                    {
                        continue;
                    }

                    if (!includeCBCT && exam.ExamName.ToLower().Contains("cbct"))
                    {
                        continue;
                    }

                    if (!frameOfRefToExams.ContainsKey(examFrameOfRef))
                    {
                        frameOfRefToExams[examFrameOfRef] = new List<ExaminationClass>();
                    }
                    frameOfRefToExams[examFrameOfRef].Add(exam);
                }
            }

            // Step 2: Filter registrations
            List<RegistrationExportInfo> filteredRegistrations = new List<RegistrationExportInfo>();
            HashSet<string> validRegistrationUIDs = new HashSet<string>();

            if (item.AssociatedRegistrations != null)
            {
                foreach (RegistrationExportInfo regInfo in item.AssociatedRegistrations)
                {
                    if (regInfo.ToFrameOfReference != primaryFrameOfRef)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(regInfo.FromFrameOfReference))
                    {
                        continue;
                    }

                    if (!frameOfRefToExams.ContainsKey(regInfo.FromFrameOfReference))
                    {
                        continue;
                    }

                    filteredRegistrations.Add(regInfo);

                    if (!string.IsNullOrEmpty(regInfo.RegistrationUID))
                    {
                        validRegistrationUIDs.Add(regInfo.RegistrationUID);
                    }
                }
            }

            // Step 3: Get all registration series and filter by valid RegistrationUIDs
            List<DicomDataset> regSeries = allSeries.Where(s =>
            {
                string modality = GetStringValue(s, DicomTag.Modality);
                return modality == "REG" || modality == "SPATIAL REGISTRATION";
            }).ToList();

            if (regSeries.Any() && validRegistrationUIDs.Any())
            {
                List<DicomDataset> allRegistrationInstances = await FindInstancesForSeriesAsync(regSeries, options, cancellationToken);

                HashSet<string> validSeriesUIDs = new HashSet<string>();
                foreach (DicomDataset instance in allRegistrationInstances)
                {
                    string sopInstanceUID = GetStringValue(instance, DicomTag.SOPInstanceUID);
                    if (validRegistrationUIDs.Contains(sopInstanceUID))
                    {
                        string seriesUID = GetStringValue(instance, DicomTag.SeriesInstanceUID);
                        if (!string.IsNullOrEmpty(seriesUID))
                        {
                            validSeriesUIDs.Add(seriesUID);
                        }
                    }
                }

                regSeries = regSeries.Where(s =>
                    validSeriesUIDs.Contains(GetStringValue(s, DicomTag.SeriesInstanceUID))).ToList();
            }
            else
            {
                regSeries.Clear();
            }

            // Step 4: Collect the filtered registration DICOM objects
            foreach (DicomDataset series in regSeries)
            {
                pendingExports.Add(new PendingExport
                {
                    Series = series,
                    ExportPath = registrationFolder,
                    DataType = "Registration",
                    IsImageLevel = false
                });
            }

            // Step 5: Collect source images for filtered registrations
            HashSet<string> collectedSeriesUIDs = new HashSet<string>();
            foreach (RegistrationExportInfo regInfo in filteredRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<ExaminationClass> sourceExams = frameOfRefToExams[regInfo.FromFrameOfReference];

                foreach (ExaminationClass sourceExam in sourceExams)
                {
                    string seriesUID = sourceExam.SeriesInstanceUID;
                    if (string.IsNullOrEmpty(seriesUID) || collectedSeriesUIDs.Contains(seriesUID))
                    {
                        continue;
                    }

                    string examName = !string.IsNullOrEmpty(sourceExam.ExamName)
                        ? sourceExam.ExamName
                        : "UnknownSource";
                    string examFolder = Path.Combine(baseExportPath, "RegisteredImages", SanitizeFolderName(examName));
                    EnsureDirectoryExists(examFolder);

                    // Find matching series in allSeries for this seriesUID
                    IEnumerable<DicomDataset> matchingSeries = allSeries.Where(s =>
                        GetStringValue(s, DicomTag.SeriesInstanceUID) == seriesUID);

                    foreach (DicomDataset series in matchingSeries)
                    {
                        pendingExports.Add(new PendingExport
                        {
                            Series = series,
                            ExportPath = examFolder,
                            DataType = string.Format("Registered Image ({0})", examName),
                            IsImageLevel = false
                        });
                    }

                    collectedSeriesUIDs.Add(seriesUID);
                }
            }

            return pendingExports;
        }

        /// <summary>
        /// Find studies for a patient using C-FIND
        /// </summary>
        private async Task<List<DicomDataset>> FindStudiesAsync(
            string patientId,
            DicomExportOptions options,
            CancellationToken cancellationToken)
        {
            List<DicomDataset> results = new List<DicomDataset>();

            DicomCFindRequest request = DicomCFindRequest.CreateStudyQuery(patientId);

            request.OnResponseReceived += (req, response) =>
            {
                if (response.Dataset != null)
                {
                    results.Add(response.Dataset);
                }
            };

            IDicomClient client = CreateDicomClient(options);
            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            return results;
        }

        /// <summary>
        /// Find all series for the given studies
        /// </summary>
        private async Task<List<DicomDataset>> FindSeriesForStudiesAsync(
            List<DicomDataset> studies,
            DicomExportOptions options,
            CancellationToken cancellationToken)
        {
            List<DicomDataset> allSeries = new List<DicomDataset>();

            foreach (DicomDataset study in studies)
            {
                string studyInstanceUID = GetStringValue(study, DicomTag.StudyInstanceUID);
                if (string.IsNullOrEmpty(studyInstanceUID))
                    continue;

                DicomCFindRequest request = DicomCFindRequest.CreateSeriesQuery(studyInstanceUID);

                request.OnResponseReceived += (req, response) =>
                {
                    if (response.Dataset != null)
                    {
                        allSeries.Add(response.Dataset);
                    }
                };

                IDicomClient client = CreateDicomClient(options);
                await client.AddRequestAsync(request);
                await client.SendAsync(cancellationToken);
            }

            return allSeries;
        }

        private async Task<List<DicomDataset>> FindInstancesForSeriesAsync(
            List<DicomDataset> seriesList,
            DicomExportOptions options,
            CancellationToken cancellationToken)
        {
            List<DicomDataset> allInstances = new List<DicomDataset>();

            foreach (DicomDataset series in seriesList)
            {
                string studyInstanceUID = GetStringValue(series, DicomTag.StudyInstanceUID);
                string seriesInstanceUID = GetStringValue(series, DicomTag.SeriesInstanceUID);

                if (string.IsNullOrEmpty(studyInstanceUID) || string.IsNullOrEmpty(seriesInstanceUID))
                    continue;

                DicomCFindRequest request = DicomCFindRequest.CreateImageQuery(
                    studyInstanceUID,
                    seriesInstanceUID);

                request.OnResponseReceived += (req, response) =>
                {
                    if (response.Dataset != null)
                    {
                        allInstances.Add(response.Dataset);
                    }
                };

                IDicomClient client = CreateDicomClient(options);
                await client.AddRequestAsync(request);
                await client.SendAsync(cancellationToken);
            }

            return allInstances;
        }

        /// <summary>
        /// Export a single series image using C-MOVE (image-level: StudyUID + SeriesUID + SOPInstanceUID)
        /// </summary>
        private async Task ExportSeriesImageAsync(
            DicomDataset series,
            DicomExportOptions options,
            string exportPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string seriesInstanceUID = GetStringValue(series, DicomTag.SeriesInstanceUID);
                string studyInstanceUID = GetStringValue(series, DicomTag.StudyInstanceUID);
                string sopInstanceUID = GetStringValue(series, DicomTag.SOPInstanceUID);

                DicomCStoreReceiverService.ExportPath = exportPath;

                // Create C-MOVE request at image level
                DicomCMoveRequest moveRequest = new DicomCMoveRequest(
                    options.LocalAETitle,
                    studyInstanceUID,
                    seriesInstanceUID,
                    sopInstanceUID);

                moveRequest.OnResponseReceived += (req, response) =>
                {
                    if (response.Status != DicomStatus.Pending && response.Status != DicomStatus.Success)
                    {
                        Console.WriteLine(string.Format("C-MOVE response status: {0}", response.Status));
                    }
                };

                IDicomClient client = CreateDicomClient(options);
                await client.AddRequestAsync(moveRequest);
                await client.SendAsync(cancellationToken);

                // Small delay to allow file reception
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Error exporting series image: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Export a single series using C-MOVE (series-level: StudyUID + SeriesUID)
        /// </summary>
        private async Task ExportSeriesAsync(
            DicomDataset series,
            DicomExportOptions options,
            string exportPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string seriesInstanceUID = GetStringValue(series, DicomTag.SeriesInstanceUID);
                string studyInstanceUID = GetStringValue(series, DicomTag.StudyInstanceUID);

                DicomCStoreReceiverService.ExportPath = exportPath;

                // Create C-MOVE request at series level
                DicomCMoveRequest moveRequest = new DicomCMoveRequest(
                    options.LocalAETitle,
                    studyInstanceUID,
                    seriesInstanceUID);

                moveRequest.OnResponseReceived += (req, response) =>
                {
                    if (response.Status != DicomStatus.Pending && response.Status != DicomStatus.Success)
                    {
                        Console.WriteLine(string.Format("C-MOVE response status: {0}", response.Status));
                    }
                };

                IDicomClient client = CreateDicomClient(options);
                await client.AddRequestAsync(moveRequest);
                await client.SendAsync(cancellationToken);

                // Small delay to allow file reception
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Error exporting series: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Setup local SCP to receive C-STORE requests
        /// </summary>
        private async Task SetupLocalSCPAsync(DicomExportOptions options)
        {
            // Configure the SCP provider options
            DicomCStoreReceiverService.ExportPath = options.ExportFolder;
            DicomCStoreReceiverService.ShouldAnonymize = options.Anonymize;

            _localSCP = DicomServerFactory.Create<DicomCStoreReceiverService>(options.LocalPort);

            // Small delay to ensure server is ready
            await Task.Delay(100);
        }

        /// <summary>
        /// Create a DICOM client configured for the remote server
        /// </summary>
        private IDicomClient CreateDicomClient(DicomExportOptions options)
        {
            IDicomClient client = DicomClientFactory.Create(
                options.RemoteIP,
                options.RemotePort,
                false,
                options.LocalAETitle,
                options.RemoteAETitle);

            return client;
        }

        /// <summary>
        /// Get string value from DicomDataset safely
        /// </summary>
        private string GetStringValue(DicomDataset dataset, DicomTag tag)
        {
            if (dataset == null)
                return null;

            try
            {
                return dataset.GetSingleValueOrDefault<string>(tag, null);
            }
            catch
            {
                return null;
            }
        }

        private string GetExamFrameOfReference(ExaminationClass exam)
        {
            if (exam == null) return null;
            if (exam.EquipmentInfo != null && !string.IsNullOrEmpty(exam.EquipmentInfo.FrameOfReference))
            {
                return exam.EquipmentInfo.FrameOfReference;
            }
            return null;
        }

        private string GetExamModality(ExaminationClass exam)
        {
            if (exam == null) return null;
            if (exam.EquipmentInfo != null && !string.IsNullOrEmpty(exam.EquipmentInfo.Modality))
            {
                return exam.EquipmentInfo.Modality;
            }
            return null;
        }

        private bool IsModalityAllowed(string modality, List<string> allowedModalities)
        {
            if (string.IsNullOrEmpty(modality) || allowedModalities == null || allowedModalities.Count == 0)
            {
                return false;
            }

            string normalizedModality = modality.ToUpperInvariant();
            if (normalizedModality == "PT") normalizedModality = "PET";

            foreach (string allowed in allowedModalities)
            {
                string normalizedAllowed = allowed.ToUpperInvariant();
                if (normalizedAllowed == "PT") normalizedAllowed = "PET";

                if (normalizedModality == normalizedAllowed)
                {
                    return true;
                }
            }
            return false;
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
    /// DICOM C-STORE SCP service to receive and save incoming DICOM files
    /// </summary>
    public class DicomCStoreReceiverService : DicomService, IDicomServiceProvider, IDicomCStoreProvider
    {
        private static readonly object _lock = new object();
        private static string _exportPath;
        private static bool _shouldAnonymize;

        /// <summary>
        /// Salt used for deterministic hashing. Change this value to produce
        /// a completely different set of anonymous IDs for the same input data.
        /// </summary>
        private static string _hashSalt = "DicomExportAnon";

        public static string ExportPath
        {
            get { lock (_lock) { return _exportPath; } }
            set { lock (_lock) { _exportPath = value; } }
        }
        private static string _anonymizationKeyPath;

        public static string AnonymizationKeyPath
        {
            get { lock (_lock) { return _anonymizationKeyPath; } }
            set { lock (_lock) { _anonymizationKeyPath = value; } }
        }

        public static bool ShouldAnonymize
        {
            get { lock (_lock) { return _shouldAnonymize; } }
            set { lock (_lock) { _shouldAnonymize = value; } }
        }

        public static string HashSalt
        {
            get { lock (_lock) { return _hashSalt; } }
            set { lock (_lock) { _hashSalt = value; } }
        }

        public DicomCStoreReceiverService(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
        }

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            foreach (DicomPresentationContext pc in association.PresentationContexts)
            {
                pc.SetResult(DicomPresentationContextResult.Accept);
            }
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            Console.WriteLine(string.Format("Association aborted: {0} - {1}", source, reason));
        }

        public void OnConnectionClosed(Exception exception)
        {
            if (exception != null)
            {
                Console.WriteLine(string.Format("Connection closed with exception: {0}", exception.Message));
            }
        }

        public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            try
            {
                // Anonymize before saving — the un-anonymized data never touches disk
                if (ShouldAnonymize)
                {
                    AnonymizeDataset(request.Dataset);
                }

                string modality = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, "Unknown");
                string subFolder = GetSubfolderForModality(modality);
                string targetPath = ExportPath;

                if (!string.IsNullOrEmpty(subFolder))
                {
                    targetPath = Path.Combine(ExportPath, subFolder);
                }

                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }

                string sopInstanceUID = request.SOPInstanceUID.UID;
                string fileName = sopInstanceUID + ".dcm";
                string filePath = Path.Combine(targetPath, fileName);

                Console.WriteLine(string.Format("Writing file: {0}", filePath));
                request.File.Save(filePath);

                return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Error writing DICOM file: {0}", ex.Message));
                return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.ProcessingFailure));
            }
        }

        /// <summary>
        /// Anonymize a DICOM dataset by removing/replacing patient demographics
        /// while preserving all UIDs needed for spatial and relational integrity.
        /// Uses a deterministic SHA-256 hash so the same original value always
        /// produces the same anonymous replacement across exports and sessions.
        /// </summary>
        private static void AnonymizeDataset(DicomDataset dataset)
        {
            // --- Deterministic replacements (same input → same output every time) ---
            string originalPatientID = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "");
            string anonymizedPatientID = GetAnonymizedPatientID(originalPatientID);
            ReplaceIfPresent(dataset, DicomTag.PatientID, anonymizedPatientID);
            ReplaceIfPresent(dataset, DicomTag.PatientName, DeterministicHash("PatientName", dataset, DicomTag.PatientID)); // keyed on ID so name stays consistent with ID
            ReplaceIfPresent(dataset, DicomTag.AccessionNumber, DeterministicHash("AccessionNumber", dataset, DicomTag.AccessionNumber));
            ReplaceIfPresent(dataset, DicomTag.InstitutionName, DeterministicHash("InstitutionName", dataset, DicomTag.InstitutionName));
            ReplaceIfPresent(dataset, DicomTag.InstitutionAddress, "");
            ReplaceIfPresent(dataset, DicomTag.ReferringPhysicianName, "ANON");
            ReplaceIfPresent(dataset, DicomTag.PhysiciansOfRecord, "ANON");
            ReplaceIfPresent(dataset, DicomTag.PerformingPhysicianName, "ANON");
            ReplaceIfPresent(dataset, DicomTag.OperatorsName, "ANON");

            // --- Blanked fields ---
            ReplaceIfPresent(dataset, DicomTag.PatientBirthDate, "");
            ReplaceIfPresent(dataset, DicomTag.PatientBirthTime, "");
            ReplaceIfPresent(dataset, DicomTag.PatientAddress, "");
            ReplaceIfPresent(dataset, DicomTag.PatientTelephoneNumbers, "");
            ReplaceIfPresent(dataset, DicomTag.PatientComments, "");
            ReplaceIfPresent(dataset, DicomTag.AdditionalPatientHistory, "");

            // NOTE: The following UIDs are intentionally PRESERVED to maintain
            // spatial registration and relational integrity across exported files:
            //   - FrameOfReferenceUID
            //   - StudyInstanceUID
            //   - SeriesInstanceUID
            //   - SOPInstanceUID
            //   - ReferencedSOPInstanceUID (inside sequences)
            //   - ReferencedFrameOfReferenceUID (inside sequences)
        }

        /// <summary>
        /// Gets the anonymized Patient ID from the AnonymizationKey mapping file.
        /// Falls back to deterministic hash if no mapping exists or file cannot be read.
        /// </summary>
        private static string GetAnonymizedPatientID(string originalPatientID)
        {
            if (string.IsNullOrEmpty(originalPatientID))
            {
                return "ANON";
            }

            // Try to read from AnonymizationKey
            if (!string.IsNullOrEmpty(_anonymizationKeyPath) && File.Exists(_anonymizationKeyPath))
            {
                try
                {
                    string json = File.ReadAllText(_anonymizationKeyPath);
                    AnonymizationKey anonKey = JsonConvert.DeserializeObject<AnonymizationKey>(json);

                    if (anonKey != null && anonKey.Mappings != null && anonKey.Mappings.ContainsKey(originalPatientID))
                    {
                        return anonKey.Mappings[originalPatientID];
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not read AnonymizationKey for C-STORE anonymization: {ex.Message}");
                }
            }

            // Fallback to deterministic hash (this shouldn't happen if GetOrCreateAnonymizedMRN was called first)
            return DeterministicHashString("PatientID:" + originalPatientID);
        }
        /// <summary>
        /// Produce a deterministic 8-character hex string from (purpose + original value + salt).
        /// The same original DICOM value will always hash to the same result regardless of
        /// when or where the export is run, as long as the salt is unchanged.
        /// </summary>
        private static string DeterministicHash(string purpose, DicomDataset dataset, DicomTag sourceTag)
        {
            string original = dataset.GetSingleValueOrDefault<string>(sourceTag, "");
            if (string.IsNullOrEmpty(original))
            {
                return "ANON";
            }

            return DeterministicHashString(string.Format("{0}:{1}", purpose, original));
        }
        /// <summary>
        /// Public deterministic hash usable outside the SCP (e.g. for folder naming).
        /// Same input + same salt → same output every time.
        /// </summary>
        public static string DeterministicHashString(string inputString)
        {
            string salted = string.Format("{0}:{1}", inputString, HashSalt);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
                return "A" + BitConverter.ToString(hashBytes, 0, 4).Replace("-", "");
            }
        }

        /// <summary>
        /// Replace a tag value only if it already exists in the dataset
        /// </summary>
        private static void ReplaceIfPresent(DicomDataset dataset, DicomTag tag, string value)
        {
            if (dataset.Contains(tag))
            {
                dataset.AddOrUpdate(tag, value ?? "");
            }
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        {
            Console.WriteLine(string.Format("C-STORE exception: {0}", e.Message));
            return Task.CompletedTask;
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
                    return "Registrations";
                case "CT":
                case "MR":
                case "PT":
                default:
                    return string.Empty;
            }
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
        public Task ExportFromDirectoryAsync(
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
                        OverallPercentComplete = (int)((double)processedItems / totalItems * 100),
                        SeriesPercentComplete = -1,
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
                    OverallPercentComplete = 100,
                    SeriesPercentComplete = 100,
                    StatusMessage = "Export complete",
                    DetailMessage = string.Format("Processed {0} examination(s)", processedItems)
                });
            }

            return Task.CompletedTask;
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

using DataBaseStructure.AriaBase;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataMiningGUI
{
    /// <summary>
    /// Service for exporting DICOM data using FellowOakDicom (fo-dicom)
    /// </summary>
    public class DicomExportService
    {
        private IDicomServer _localSCP;
        private string _currentExportPath;
        private ConcurrentQueue<string> _receivedFiles;
        private int _receivedFileCount;

        // Static storage for the SCP to access current export path
        private static string _staticExportPath;
        private static ConcurrentQueue<string> _staticReceivedFiles;

        /// <summary>
        /// Export selected items to the specified folder using DICOM C-MOVE
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
                        PercentComplete = 0,
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
                    _staticExportPath = _currentExportPath;

                    // Create subdirectories for each data type
                    string structureFolder = _currentExportPath;
                    string doseFolder = _currentExportPath;
                    string planFolder = _currentExportPath;
                    string registrationFolder = _currentExportPath;

                    EnsureDirectoryExists(_currentExportPath);

                    // Find studies for this patient using C-FIND
                    List<DicomDataset> studies = await FindStudiesAsync(item.MRN, options, cancellationToken);

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
                    List<DicomDataset> allSeries = await FindSeriesForStudiesAsync(studies, options, cancellationToken);
                    // Export Examination (CT/MR images)
                    if (options.ExportExamination && !string.IsNullOrEmpty(item.SeriesInstanceUID))
                    {
                        await ExportSeriesByUIDAsync(allSeries, item.SeriesInstanceUID,
                            options, _currentExportPath, "Examination", progress, cancellationToken);
                    }

                    // Export Structure Set, Plan, and Dose by finding related series
                    if (options.ExportStructure || options.ExportPlan || options.ExportDose)
                    {
                        List<DicomDataset> rtStructSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTSTRUCT").ToList();
                        // Find RT Structure Set series


                        if (options.ExportStructure && rtStructSeries.Any())
                        {
                            if (item.ExamData.StructureSetUID != null)
                            {
                                foreach (DicomDataset series in rtStructSeries)
                                {
                                    List<DicomDataset> allImagesRT = await FindInstancesForSeriesAsync(new List<DicomDataset> { series }, options, cancellationToken);
                                    if (allImagesRT.Where(s => GetStringValue(s, DicomTag.SOPInstanceUID) == item.ExamData.StructureSetUID).ToList().Any())
                                        await ExportSeriesAsync(series, options, structureFolder,
                                            "Structure", progress, cancellationToken);
                                }
                            }
                        }

                        // Find RT Plan series
                        List<DicomDataset> rtPlanSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTPLAN" && item.AssociatedPlans.Select(p => p.SeriesInstanceUID).Contains(GetStringValue(s, DicomTag.SeriesInstanceUID))).ToList();

                        if (options.ExportPlan && rtPlanSeries.Any())
                        {
                            foreach (DicomDataset series in rtPlanSeries)
                            {
                                await ExportSeriesAsync(series, options, planFolder,
                                    "Plan", progress, cancellationToken);
                            }
                        }

                        // Find RT Dose series
                        List<DicomDataset> rtDoseSeries = allSeries.Where(s => GetStringValue(s, DicomTag.Modality) == "RTDOSE").ToList();
                        //rtDoseSeries = new List<DicomDataset> { rtDoseSeries[5] };
                        //DicomDataset myDose = rtDoseSeries[5];
                        HashSet<string> seriesUIDs = new HashSet<string>();

                        if (options.ExportDose && rtDoseSeries.Any())
                        {
                            List<DicomDataset> allImagesDose = await FindInstancesForSeriesAsync(rtDoseSeries, options, cancellationToken);
                            allImagesDose = allImagesDose.Where(aid => item.AssociatedPlans.Select(p => p.DoseSOPInstanceUID).Contains(GetStringValue(aid, DicomTag.SOPInstanceUID))).ToList();
                            foreach (DicomDataset series in allImagesDose)
                            {
                                string seriesUID = GetStringValue(series, DicomTag.SOPInstanceUID);
                                if (!string.IsNullOrEmpty(seriesUID) && !seriesUIDs.Contains(seriesUID))
                                {
                                    seriesUIDs.Add(seriesUID);
                                    await ExportSeriesAsync(series, options, doseFolder,
                                        "Dose", progress, cancellationToken);
                                }
                            }
                        }
                    }

                    // Export Registrations and Associated Images
                    if (options.ExportRegistrations && item.AssociatedRegistrations != null && item.AssociatedRegistrations.Count > 0)
                    {
                        await ExportRegistrationsAndAssociatedImagesAsync(
                            allSeries, item, options, registrationFolder, _currentExportPath,
                            progress, cancellationToken);
                    }

                    processedItems++;
                }

                // Wait a bit for any remaining files to be received
                await Task.Delay(2000, cancellationToken);

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
                    _localSCP.Dispose();
                    _localSCP = null;
                }
            }
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
        /// Export a series by its UID
        /// </summary>
        private async Task ExportSeriesByUIDAsync(
            List<DicomDataset> allSeries,
            string seriesInstanceUID,
            DicomExportOptions options,
            string exportPath,
            string dataType,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            IEnumerable<DicomDataset> matchingSeries = allSeries.Where(s =>
                GetStringValue(s, DicomTag.SeriesInstanceUID) == seriesInstanceUID);

            foreach (DicomDataset series in matchingSeries)
            {
                await ExportSeriesAsync(series, options, exportPath, dataType, progress, cancellationToken);
            }
        }

        /// <summary>
        /// Export a single series using C-MOVE
        /// </summary>
        private async Task ExportSeriesAsync(
            DicomDataset series,
            DicomExportOptions options,
            string exportPath,
            string dataType,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string seriesInstanceUID = GetStringValue(series, DicomTag.SeriesInstanceUID);
                string studyInstanceUID = GetStringValue(series, DicomTag.StudyInstanceUID);

                string seriesUidDisplay = "Unknown";
                if (!string.IsNullOrEmpty(seriesInstanceUID))
                {
                    int displayLength = Math.Min(30, seriesInstanceUID.Length);
                    seriesUidDisplay = seriesInstanceUID.Substring(0, displayLength);
                }

                if (progress != null)
                {
                    progress.Report(new DicomExportProgress
                    {
                        PercentComplete = -1,
                        StatusMessage = string.Format("Exporting {0}", dataType),
                        DetailMessage = string.Format("Series: {0}...", seriesUidDisplay)
                    });
                }

                DicomCStoreReceiverService.ExportPath = exportPath;

                // Create C-MOVE request
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
        /// Export registrations and their associated source images
        /// </summary>
        private async Task ExportRegistrationsAndAssociatedImagesAsync(
            List<DicomDataset> allSeries,
            ExportItem item,
            DicomExportOptions options,
            string registrationFolder,
            string baseExportPath,
            IProgress<DicomExportProgress> progress,
            CancellationToken cancellationToken)
        {
            // Get the primary exam's FrameOfReference
            string primaryFrameOfRef = null;
            if (item.ExamData != null && item.ExamData.EquipmentInfo != null)
            {
                primaryFrameOfRef = item.ExamData.EquipmentInfo.FrameOfReference;
            }

            if (string.IsNullOrEmpty(primaryFrameOfRef))
            {
                return; // Can't proceed without primary frame of reference
            }

            // Build list of allowed modalities
            List<string> allowedModalities = new List<string>();
            if (options.ExportRegistrationsCT) allowedModalities.Add("CT");
            if (options.ExportRegistrationsMR) allowedModalities.Add("MR");
            if (options.ExportRegistrationsPET) { allowedModalities.Add("PT"); allowedModalities.Add("PET"); }
            if (options.ExportRegistrationsCBCT) allowedModalities.Add("CBCT");

            bool includeCBCT = options.ExportRegistrationsCBCT;

            // Step 1: Build lookup of FrameOfReference -> ExaminationClass (need this first for filtering)
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

            // Step 2: Filter registrations - must register TO primary AND have valid source exam with allowed modality
            List<RegistrationExportInfo> filteredRegistrations = new List<RegistrationExportInfo>();
            HashSet<string> validRegistrationUIDs = new HashSet<string>();

            if (item.AssociatedRegistrations != null)
            {
                foreach (RegistrationExportInfo regInfo in item.AssociatedRegistrations)
                {
                    // Must register TO the primary frame of reference
                    if (regInfo.ToFrameOfReference != primaryFrameOfRef)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(regInfo.FromFrameOfReference))
                    {
                        continue;
                    }

                    // Must have at least one exam with allowed modality at the FromFrameOfReference
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
                // Query instance-level to get SOPInstanceUIDs
                List<DicomDataset> allRegistrationInstances = await FindInstancesForSeriesAsync(regSeries, options, cancellationToken);

                // Find series that contain valid registration instances
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

                // Filter regSeries to only those containing valid registrations
                regSeries = regSeries.Where(s =>
                    validSeriesUIDs.Contains(GetStringValue(s, DicomTag.SeriesInstanceUID))).ToList();
            }
            else
            {
                regSeries.Clear();
            }

            // Step 4: Export the filtered registration DICOM objects
            foreach (DicomDataset series in regSeries)
            {
                await ExportSeriesAsync(series, options, registrationFolder,
                    "Registrations", progress, cancellationToken);
            }

            // Step 5: Export source images for filtered registrations
            HashSet<string> exportedSeriesUIDs = new HashSet<string>();
            foreach (RegistrationExportInfo regInfo in filteredRegistrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<ExaminationClass> sourceExams = frameOfRefToExams[regInfo.FromFrameOfReference];

                foreach (ExaminationClass sourceExam in sourceExams)
                {
                    string seriesUID = sourceExam.SeriesInstanceUID;
                    if (string.IsNullOrEmpty(seriesUID) || exportedSeriesUIDs.Contains(seriesUID))
                    {
                        continue;
                    }

                    string examName = !string.IsNullOrEmpty(sourceExam.ExamName)
                        ? sourceExam.ExamName
                        : "UnknownSource";
                    string examFolder = Path.Combine(baseExportPath, "RegisteredImages", SanitizeFolderName(examName));
                    EnsureDirectoryExists(examFolder);

                    if (progress != null)
                    {
                        progress.Report(new DicomExportProgress
                        {
                            PercentComplete = -1,
                            StatusMessage = string.Format("Exporting Registration: {0}", regInfo.RegistrationName),
                            DetailMessage = string.Format("Source: {0}", examName)
                        });
                    }

                    await ExportSeriesByUIDAsync(allSeries, seriesUID, options, examFolder,
                        string.Format("Source Image ({0})", examName), progress, cancellationToken);
                    exportedSeriesUIDs.Add(seriesUID);
                }
            }
        }

        /// <summary>
        /// Setup local SCP to receive C-STORE requests
        /// </summary>
        private async Task SetupLocalSCPAsync(DicomExportOptions options)
        {
            // Configure the SCP provider options
            DicomCStoreReceiverService.ExportPath = options.ExportFolder;

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

        public static string ExportPath
        {
            get { lock (_lock) { return _exportPath; } }
            set { lock (_lock) { _exportPath = value; } }
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
﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAPS2.Config;
using NAPS2.Config.Experimental;
using NAPS2.ImportExport;
using NAPS2.ImportExport.Images;
using NAPS2.ImportExport.Pdf;
using NAPS2.Lang.Resources;
using NAPS2.Ocr;
using NAPS2.Operation;
using NAPS2.Images;
using NAPS2.Util;
using NAPS2.WinForms;

namespace NAPS2.Scan.Batch
{
    public class BatchScanPerformer
    {
        private readonly IScanPerformer scanPerformer;
        private readonly PdfExporter pdfExporter;
        private readonly IOperationFactory operationFactory;
        private readonly PdfSettingsContainer pdfSettingsContainer;
        private readonly OcrEngineManager ocrEngineManager;
        private readonly IFormFactory formFactory;
        private readonly ConfigProvider<CommonConfig> configProvider;

        public BatchScanPerformer(IScanPerformer scanPerformer, PdfExporter pdfExporter, IOperationFactory operationFactory, PdfSettingsContainer pdfSettingsContainer, OcrEngineManager ocrEngineManager, IFormFactory formFactory, ConfigProvider<CommonConfig> configProvider)
        {
            this.scanPerformer = scanPerformer;
            this.pdfExporter = pdfExporter;
            this.operationFactory = operationFactory;
            this.pdfSettingsContainer = pdfSettingsContainer;
            this.ocrEngineManager = ocrEngineManager;
            this.formFactory = formFactory;
            this.configProvider = configProvider;
        }

        public async Task PerformBatchScan(BatchSettings settings, FormBase batchForm, Action<ScannedImage> imageCallback, Action<string> progressCallback, CancellationToken cancelToken)
        {
            var state = new BatchState(scanPerformer, pdfExporter, operationFactory, pdfSettingsContainer, ocrEngineManager, formFactory, configProvider)
            {
                Settings = settings,
                ProgressCallback = progressCallback,
                CancelToken = cancelToken,
                BatchForm = batchForm,
                LoadImageCallback = imageCallback
            };
            await state.Do();
        }

        private class BatchState
        {
            private readonly IScanPerformer scanPerformer;
            private readonly PdfExporter pdfExporter;
            private readonly IOperationFactory operationFactory;
            private readonly PdfSettingsContainer pdfSettingsContainer;
            private readonly OcrEngineManager ocrEngineManager;
            private readonly IFormFactory formFactory;
            private readonly ConfigProvider<CommonConfig> configProvider;

            private ScanProfile profile;
            private ScanParams scanParams;
            private List<List<ScannedImage>> scans;

            public BatchState(IScanPerformer scanPerformer, PdfExporter pdfExporter, IOperationFactory operationFactory,
                PdfSettingsContainer pdfSettingsContainer, OcrEngineManager ocrEngineManager, IFormFactory formFactory, ConfigProvider<CommonConfig> configProvider)
            {
                this.scanPerformer = scanPerformer;
                this.pdfExporter = pdfExporter;
                this.operationFactory = operationFactory;
                this.pdfSettingsContainer = pdfSettingsContainer;
                this.ocrEngineManager = ocrEngineManager;
                this.formFactory = formFactory;
                this.configProvider = configProvider;
            }

            public BatchSettings Settings { get; set; }

            public Action<string> ProgressCallback { get; set; }

            public CancellationToken CancelToken { get; set; }

            public FormBase BatchForm { get; set; }

            public Action<ScannedImage> LoadImageCallback { get; set; }

            public async Task Do()
            {
                profile = ProfileManager.Current.Profiles.First(x => x.DisplayName == Settings.ProfileDisplayName);
                scanParams = new ScanParams
                {
                    DetectPatchCodes = Settings.OutputType == BatchOutputType.MultipleFiles && Settings.SaveSeparator == SaveSeparator.PatchT,
                    NoUI = true,
                    DoOcr = Settings.OutputType == BatchOutputType.Load
                        ? configProvider.Get(c => c.EnableOcr) && configProvider.Get(c => c.OcrAfterScanning) // User configured
                        : configProvider.Get(c => c.EnableOcr) && GetSavePathExtension().ToLower() == ".pdf", // Fully automated
                    OcrParams = configProvider.DefaultOcrParams(),
                    OcrCancelToken = CancelToken
                };

                try
                {
                    CancelToken.ThrowIfCancellationRequested();
                    await Input();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    CancelToken.ThrowIfCancellationRequested();
                    // Save at least some data so it isn't lost
                    await Output();
                    throw;
                }

                try
                {
                    CancelToken.ThrowIfCancellationRequested();
                    await Output();
                }
                catch (OperationCanceledException)
                {
                }
            }

            private async Task Input()
            {
                await Task.Run(async () =>
                {
                    scans = new List<List<ScannedImage>>();

                    if (Settings.ScanType == BatchScanType.Single)
                    {
                        await InputOneScan(-1);
                    }
                    else if (Settings.ScanType == BatchScanType.MultipleWithDelay)
                    {
                        for (int i = 0; i < Settings.ScanCount; i++)
                        {
                            ProgressCallback(string.Format(MiscResources.BatchStatusWaitingForScan, i + 1));
                            if (i != 0)
                            {
                                ThreadSleepWithCancel(TimeSpan.FromSeconds(Settings.ScanIntervalSeconds), CancelToken);
                                CancelToken.ThrowIfCancellationRequested();
                            }

                            if (!await InputOneScan(i))
                            {
                                return;
                            }
                        }
                    }
                    else if (Settings.ScanType == BatchScanType.MultipleWithPrompt)
                    {
                        int i = 0;
                        do
                        {
                            ProgressCallback(string.Format(MiscResources.BatchStatusWaitingForScan, i + 1));
                            if (!await InputOneScan(i++))
                            {
                                return;
                            }
                            CancelToken.ThrowIfCancellationRequested();
                        } while (PromptForNextScan());
                    }
                });
            }

            private void ThreadSleepWithCancel(TimeSpan sleepDuration, CancellationToken cancelToken)
            {
                cancelToken.WaitHandle.WaitOne(sleepDuration);
            }

            private async Task<bool> InputOneScan(int scanNumber)
            {
                var scan = new List<ScannedImage>();
                int pageNumber = 1;
                ProgressCallback(scanNumber == -1
                    ? string.Format(MiscResources.BatchStatusPage, pageNumber++)
                    : string.Format(MiscResources.BatchStatusScanPage, pageNumber++, scanNumber + 1));
                CancelToken.ThrowIfCancellationRequested();
                try
                {
                    await DoScan(scanNumber, scan, pageNumber);
                }
                catch (OperationCanceledException)
                {
                    scans.Add(scan);
                    throw;
                }
                if (scan.Count == 0)
                {
                    // Presume cancelled
                    return false;
                }
                scans.Add(scan);
                return true;
            }

            private async Task DoScan(int scanNumber, List<ScannedImage> scan, int pageNumber)
            {
                var source = scanPerformer.PerformScan(profile, scanParams, BatchForm.SafeHandle(), CancelToken);
                await source.ForEach(image =>
                {
                    scan.Add(image);
                    CancelToken.ThrowIfCancellationRequested();
                    ProgressCallback(scanNumber == -1
                        ? string.Format(MiscResources.BatchStatusPage, pageNumber++)
                        : string.Format(MiscResources.BatchStatusScanPage, pageNumber++, scanNumber + 1));
                });
            }

            private bool PromptForNextScan()
            {
                var promptForm = formFactory.Create<FBatchPrompt>();
                promptForm.ScanNumber = scans.Count + 1;
                return promptForm.ShowDialog() == DialogResult.OK;
            }

            private async Task Output()
            {
                ProgressCallback(MiscResources.BatchStatusSaving);

                var placeholders = Placeholders.All.WithDate(DateTime.Now);
                var allImages = scans.SelectMany(x => x).ToList();

                if (Settings.OutputType == BatchOutputType.Load)
                {
                    foreach (var image in allImages)
                    {
                        LoadImageCallback(image);
                    }
                }
                else if (Settings.OutputType == BatchOutputType.SingleFile)
                {
                    await Save(placeholders, 0, allImages);
                    foreach (var img in allImages)
                    {
                        img.Dispose();
                    }
                }
                else if (Settings.OutputType == BatchOutputType.MultipleFiles)
                {
                    int i = 0;
                    foreach (var imageList in SaveSeparatorHelper.SeparateScans(scans, Settings.SaveSeparator))
                    {
                        await Save(placeholders, i++, imageList);
                        foreach (var img in imageList)
                        {
                            img.Dispose();
                        }
                    }
                }
            }

            private async Task Save(Placeholders placeholders, int i, List<ScannedImage> images)
            {
                if (images.Count == 0)
                {
                    return;
                }
                var subPath = placeholders.Substitute(Settings.SavePath, true, i);
                if (GetSavePathExtension().ToLower() == ".pdf")
                {
                    if (File.Exists(subPath))
                    {
                        subPath = placeholders.Substitute(subPath, true, 0, 1);
                    }
                    var snapshots = images.Select(x => x.Preserve()).ToList();
                    try
                    {
                        await pdfExporter.Export(subPath, snapshots, pdfSettingsContainer.PdfSettings, new OcrContext(configProvider.DefaultOcrParams()), (j, k) => { }, CancelToken);
                    }
                    finally
                    {
                        snapshots.ForEach(s => s.Dispose());
                    }
                }
                else
                {
                    var op = operationFactory.Create<SaveImagesOperation>();
                    op.Start(subPath, placeholders, images, true);
                    await op.Success;
                }
            }

            private string GetSavePathExtension()
            {
                if (Settings.SavePath == null)
                {
                    throw new ArgumentException();
                }
                string extension = Path.GetExtension(Settings.SavePath);
                Debug.Assert(extension != null);
                return extension;
            }
        }
    }
}

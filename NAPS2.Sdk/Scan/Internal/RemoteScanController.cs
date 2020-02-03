﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAPS2.Images;
using NAPS2.Images.Storage;
using NAPS2.Util;

namespace NAPS2.Scan.Internal
{
    internal class RemoteScanController : IRemoteScanController
    {
        private readonly IScanDriverFactory scanDriverFactory;
        private readonly IRemotePostProcessor remotePostProcessor;

        public RemoteScanController()
          : this(new ScanDriverFactory(ImageContext.Default), new RemotePostProcessor())
        {
        }

        public RemoteScanController(ImageContext imageContext)
            : this(new ScanDriverFactory(imageContext), new RemotePostProcessor(imageContext))
        {
        }

        public RemoteScanController(IScanDriverFactory scanDriverFactory, IRemotePostProcessor remotePostProcessor)
        {
            this.scanDriverFactory = scanDriverFactory;
            this.remotePostProcessor = remotePostProcessor;
        }

        public async Task<List<ScanDevice>> GetDeviceList(ScanOptions options)
        {
            var deviceList = await scanDriverFactory.Create(options).GetDeviceList(options);
            if (options.Driver == Driver.Twain && !options.TwainOptions.IncludeWiaDevices)
            {
                deviceList = deviceList.Where(x => !x.ID.StartsWith("WIA-", StringComparison.InvariantCulture)).ToList();
            }
            return deviceList;
        }

        public async Task Scan(ScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents, Action<ScannedImage, PostProcessingContext> callback)
        {
            var driver = scanDriverFactory.Create(options);
            var progressThrottle = new EventThrottle<double>(scanEvents.PageProgress);
            var driverScanEvents = new ScanEvents(scanEvents.PageStart, progressThrottle.OnlyIfChanged);
            int pageNumber = 0;
            await driver.Scan(options, cancelToken, driverScanEvents, image =>
            {
                var postProcessingContext = new PostProcessingContext
                {
                    PageNumber = ++pageNumber
                };
                var scannedImage = remotePostProcessor.PostProcess(image, options, postProcessingContext);
                callback(scannedImage, postProcessingContext);
            });
        }
    }
}

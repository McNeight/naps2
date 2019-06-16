﻿using System;
using System.Collections.Generic;
using System.Linq;
using NAPS2.Images.Transforms;
using NAPS2.Scan.Experimental;

namespace NAPS2.Images.Storage
{
    public interface IImageMetadata : IDisposable
    {
        List<Transform> TransformList { get; set; }

        int TransformState { get; set; }

        int Index { get; set; }

        BitDepth BitDepth { get; set; }

        bool Lossless { get; set; }

        void Commit();

        bool CanSerialize { get; }

        string Serialize();

        void Deserialize(string serializedData);

        IImageMetadata Clone();
    }
}
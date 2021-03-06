﻿using Microsoft.Extensions.FileProviders;
using Orchard.Environment.Extensions.Features;
using System;

namespace Orchard.Environment.Extensions
{
    public class ExtensionInfo : IExtensionInfo
    {
        private readonly IFileInfo _fileInfo;
        private readonly string _subPath;
        private readonly IManifestInfo _manifestInfo;
        private readonly IFeatureInfoList _features;

        public ExtensionInfo(
            IFileInfo fileInfo,
            string subPath,
            IManifestInfo manifestInfo,
            Func<IExtensionInfo, IFeatureInfoList> features) {

            _fileInfo = fileInfo;
            _subPath = subPath;
            _manifestInfo = manifestInfo;
            _features = features(this);
        }

        public string Id => _fileInfo.Name;
        public IFileInfo ExtensionFileInfo => _fileInfo;
        public string SubPath => _subPath;
        public IManifestInfo Manifest => _manifestInfo;
        public IFeatureInfoList Features => _features;
        public bool Exists => _fileInfo.Exists && _manifestInfo.Exists;
    }
}

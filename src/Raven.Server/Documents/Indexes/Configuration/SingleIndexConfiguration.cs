﻿using System;
using System.Reflection;
using Raven.Abstractions.Data;
using Raven.Client.Indexing;
using Raven.Server.Config;
using Raven.Server.Config.Categories;
using Raven.Server.Utils;

namespace Raven.Server.Documents.Indexes.Configuration
{
    public class SingleIndexConfiguration : IndexingConfiguration
    {
        private bool? _runInMemory;
        private string _indexStoragePath;
        private string _tempPath;
        private string _journalsStoragePath;

        private readonly RavenConfiguration _databaseConfiguration;

        public SingleIndexConfiguration(IndexConfiguration clientConfiguration, RavenConfiguration databaseConfiguration)
            : base(() => databaseConfiguration.DatabaseName, null, null)
        {
            _databaseConfiguration = databaseConfiguration;

            Initialize(key => clientConfiguration.GetValue(key) ?? databaseConfiguration.GetSetting(key), throwIfThereIsNoSetMethod: false);

            Validate();
        }

        private void Validate()
        {
            if (string.Equals(StoragePath, _databaseConfiguration.Indexing.StoragePath, StringComparison.OrdinalIgnoreCase))
                return;

            if (_databaseConfiguration.Indexing.AdditionalIndexStoragePaths != null)
            {
                foreach (var path in _databaseConfiguration.Indexing.AdditionalIndexStoragePaths)
                {
                    if (string.Equals(StoragePath, path, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            throw new InvalidOperationException($"Given index path ('{StoragePath}') is not defined in '{Constants.Configuration.Indexing.StoragePath}' or '{Constants.Configuration.Indexing.AdditionalIndexStoragePaths}'");
        }

        public override bool Disabled => _databaseConfiguration.Indexing.Disabled;

        public override bool RunInMemory
        {
            get
            {
                if (_runInMemory == null)
                    _runInMemory = _databaseConfiguration.Indexing.RunInMemory;

                return _runInMemory.Value;
            }

            protected set { _runInMemory = value; }
        }

        public override string StoragePath
        {
            get
            {
                if (string.IsNullOrEmpty(_indexStoragePath))
                    _indexStoragePath = _databaseConfiguration.Indexing.StoragePath;
                return _indexStoragePath;
            }

            protected set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _indexStoragePath = null;
                    return;
                }

                _indexStoragePath = AddDatabaseNameToPathIfNeeded(value.ToFullPath());
            }
        }

        public override string TempPath
        {
            get
            {
                if (string.IsNullOrEmpty(_tempPath))
                    _tempPath = _databaseConfiguration.Indexing.TempPath;
                return _tempPath;
            }

            protected set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _tempPath = null;
                    return;
                }

                _tempPath = AddDatabaseNameToPathIfNeeded(value.ToFullPath());
            }
        }

        public override string JournalsStoragePath
        {
            get
            {
                if (string.IsNullOrEmpty(_journalsStoragePath))
                    _journalsStoragePath = _databaseConfiguration.Indexing.JournalsStoragePath;
                return _journalsStoragePath;
            }

            protected set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _journalsStoragePath = null;
                    return;
                }

                _journalsStoragePath = AddDatabaseNameToPathIfNeeded(value.ToFullPath());
            }
        }

        public override string[] AdditionalIndexStoragePaths => _databaseConfiguration.Indexing.AdditionalIndexStoragePaths;

        public IndexUpdateType CalculateUpdateType(SingleIndexConfiguration newConfiguration)
        {
            var result = IndexUpdateType.None;
            foreach (var property in GetConfigurationProperties())
            {
                var currentValue = property.GetValue(this);
                var newValue = property.GetValue(newConfiguration);

                if (Equals(currentValue, newValue))
                    continue;

                var updateTypeAttribute = property.GetCustomAttribute<IndexUpdateTypeAttribute>();

                if (updateTypeAttribute.UpdateType == IndexUpdateType.Reset)
                    return IndexUpdateType.Reset; // worst case, we do not need to check further

                result = updateTypeAttribute.UpdateType;
            }

            return result;
        }
    }
}
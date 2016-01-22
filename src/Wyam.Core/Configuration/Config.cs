﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Wyam.Common.Configuration;
using Wyam.Common.IO;
using Wyam.Common.Meta;
using Wyam.Common.Modules;
using Wyam.Common.NuGet;
using Wyam.Common.Pipelines;
using Wyam.Common.Tracing;
using Wyam.Core.NuGet;

namespace Wyam.Core.Configuration
{
    // Manages the dynamic loading of assemblies and namespaces

    // Encapsulates configuration logic
    internal class Config : IConfig, IDisposable
    {
        private readonly AssemblyCollection _assemblyCollection = new AssemblyCollection();
        private readonly AssemblyManager _assemblyManager = new AssemblyManager();
        private readonly IConfigurableFileSystem _fileSystem;
        private readonly IInitialMetadata _initialMetadata;
        private readonly IPipelineCollection _pipelines;
        private readonly PackagesCollection _packages;

        private bool _disposed;
        private ConfigScript _configScript;
        private string _fileName;
        private bool _outputScripts;
        
        public bool Configured { get; private set; }
        
        IAssemblyCollection IConfig.Assemblies => _assemblyCollection;

        IPackagesCollection IConfig.Packages => _packages;

        public IEnumerable<Assembly> Assemblies => _assemblyManager.Assemblies;

        public IEnumerable<string> Namespaces => _assemblyManager.Namespaces;

        public byte[] RawConfigAssembly => _configScript?.RawAssembly;

        public Config(IConfigurableFileSystem fileSystem, IInitialMetadata initialMetadata, IPipelineCollection pipelines)
        {
            _fileSystem = fileSystem;
            _initialMetadata = initialMetadata;
            _pipelines = pipelines;
            _packages = new PackagesCollection(fileSystem);
            
            // Manually resolve included assemblies
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            AppDomain.CurrentDomain.SetupInformation.PrivateBinPathProbe = string.Empty; // non-null means exclude application base path
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            _disposed = true;
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Config));
            }
        }

        // Setup is separated from config by a line with only '-' characters
        // If no such line exists, then the entire script is treated as config
        public void Configure(string script, bool updatePackages, string fileName, bool outputScripts)
        {
            CheckDisposed();
            if (Configured)
            {
                throw new InvalidOperationException("This engine has already been configured.");
            }
            Configured = true;
            _fileName = fileName;
            _outputScripts = outputScripts;

            // If no script, nothing else to do
            if (string.IsNullOrWhiteSpace(script))
            {
                Configure(new ConfigParts());
                return;
            }

            ConfigParts configParts = ConfigSplitter.Split(script);

            // Setup (install packages, specify additional assemblies, etc.)
            if (configParts.HasSetup)
            {
                Setup(configParts, updatePackages);
            }

            // Configuration
            Configure(configParts);
        }

        private void Setup(ConfigParts configParts, bool updatePackages)
        {
            try
            {
                // Compile and evaluate the script
                using (Trace.WithIndent().Verbose("Evaluating setup script"))
                {
                    SetupScript setupScript = new SetupScript(configParts);
                    OutputScript(SetupScript.AssemblyName, setupScript.Code);
                    setupScript.Compile();
                    setupScript.Invoke(_packages, _assemblyCollection, _fileSystem);
                }

                // Install packages
                using (Trace.WithIndent().Verbose("Installing packages"))
                {
                    _packages.InstallPackages(updatePackages);
                }
            }
            catch (Exception ex)
            {
                Trace.Error("Unexpected error during setup: {0}", ex.Message);
                throw;
            }
        }
        
        private void Configure(ConfigParts configParts)
        {
            try
            {
                HashSet<Type> moduleTypes = new HashSet<Type>();
                using (Trace.WithIndent().Verbose("Initializing scripting environment"))
                {
                    _assemblyManager.Initialize(_assemblyCollection, _packages, _fileSystem);
                    moduleTypes = _assemblyManager.GetModuleTypes();
                }
                
                using (Trace.WithIndent().Verbose("Evaluating configuration script"))
                {
                    _configScript = new ConfigScript(configParts, moduleTypes, _assemblyManager.Namespaces);
                    OutputScript(ConfigScript.AssemblyName, _configScript.Code);
                    _configScript.Compile(_assemblyManager.Assemblies);
                    _configScript.Invoke(_initialMetadata, _pipelines, _fileSystem);
                }
            }
            catch (Exception ex)
            {
                Trace.Error("Unexpected error during configuration evaluation: {0}", ex.Message);
                throw;
            }
        }

        private void OutputScript(string assemblyName, string code)
        {
            // Output only if requested
            if (_outputScripts)
            {
                File.WriteAllText(System.IO.Path.Combine(_fileSystem.RootPath.FullPath, $"{(string.IsNullOrWhiteSpace(_fileName) ? string.Empty : _fileName + ".")}{assemblyName}.cs"), code);
            }
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            // Only start resolving after we've generated the config assembly
            if (_configScript != null)
            {
                if (args.Name == _configScript.AssemblyFullName)
                {
                    return _configScript.Assembly;
                }

                Assembly assembly;
                if (_assemblyManager.TryGetAssembly(args.Name, out assembly))
                {
                    return assembly;
                }
            }
            return null;
        }
    }
}

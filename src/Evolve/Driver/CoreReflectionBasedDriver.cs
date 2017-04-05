﻿#if NETCORE

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Evolve.Utilities;
using Microsoft.Extensions.DependencyModel;

namespace Evolve.Driver
{
    /// <summary>
    ///     <para>
    ///         Base class for .NET Core database drivers loaded by reflection.
    ///     </para>
    ///     <para>
    ///         The loading strategy differs from the .NET framework because all
    ///         the assemblies needed by the driver are not in the application build folder.
    ///      </para>
    ///      <para>
    ///         This class rely on the dependency file ([appname].deps.json) of the .Net Core application.
    ///         It lists all the dependencies, as well as compilation context data and compilation dependencies.
    ///         
    ///         Once deserialized, this file gives us a <see cref="DependencyContext"/> where we can find
    ///         the path of the database driver assembly.
    ///         
    ///         Then we load the driver with <see cref="AssemblyLoadContext.LoadFromAssemblyPath(string)"/> and copy all the native dependency in a temp <see cref="WorkingDir"/>
    ///         
    ///         And now it's hack time !
    ///         We open a connection to the database in <see cref="CoreReflectionBasedDriver.CreateConnection(string)"/> to force a load 
    ///         of the driver while the application current directory is temporary changed to a folder where are stored the native dependencies.
    ///         Then we restore the previous current directory because libraries are now loaded in memory.
    ///     </para>
    /// </summary>
    public abstract class CoreReflectionBasedDriver : AssemblyLoadContext, IDriver
    {
        private const string RuntimeLibraryLoadingError = "Failed to load assembly {0} from deps file at {1}.";
        private const string DependencyContextLoadingError = "Failed to load dependency context from {0}.";
        private const string WorkingDirectoryCreationError = "Failed to create the driver temp working folder at {0}.";
        private const string NoRuntimeTargetsFound = "No <runtimeTargets> found in the deps file for the assembly: {0}.";
        private const string NoRuntimeTargetsFoundForOS = "None of the <runtimeTargets> matches the corresponding os: {0} for the assembly: {1}.";
        private const string NoRuntimeTargetsFoundForOSArchitecture = "None of the <runtimeTargets> matches the corresponding os: {0} and os architecture: {1} for the assembly: {2}.";
        private const string MultipleRuntimeTargetsFound = "Evolve can not define the correct assembly for your system: {0}";

        private string _depsFile;

        /// <summary>
        ///     Initializes a new instance of <see cref="CoreReflectionBasedDriver" /> with
        ///     the connection type name loaded from the specified assembly.
        /// </summary>
        /// <param name="driverAssemblyName"> Assembly to load the DbConnection type from. </param>
        /// <param name="connectionTypeName"> DbConnection type name. </param>
        /// <param name="depsFile"> Dependency file of the project to migrate. </param>
        /// <param name="nugetPackageDir"> Path to the NuGet package folder. </param>
        public CoreReflectionBasedDriver(string driverAssemblyName, string connectionTypeName, string depsFile, string nugetPackageDir)
        {
            _depsFile = Check.FileExists(depsFile, nameof(depsFile));
            NugetPackageDir = Check.DirectoryExists(nugetPackageDir, nameof(nugetPackageDir));
            ProjectDependencyContext = LoadDependencyContext(_depsFile);
            DriverTypeName = new AssemblyQualifiedTypeName(Check.NotNullOrEmpty(connectionTypeName, nameof(connectionTypeName)),
                                                           Check.NotNullOrEmpty(driverAssemblyName, nameof(driverAssemblyName)));
            WorkingDir = CreateWorkingDirectory();

            DbConnectionType = TypeFromLoadedAssembly();
            if (DbConnectionType == null)
            {
                DbConnectionType = TypeFromAssembly();
            }

            Resolving += CoreReflectionBasedDriver_Resolving;
        }

        #region Properties

        protected Type DbConnectionType { get; set; }

        protected AssemblyQualifiedTypeName DriverTypeName { get; }

        protected DependencyContext ProjectDependencyContext { get; }

        protected string NugetPackageDir { get; }

        protected string WorkingDir { get; }

        #endregion

        /// <summary>
        ///     <para>
        ///         Creates an IDbConnection object for the specific Driver.
        ///     </para>
        ///     <para>
        ///         The connectionString is used to open a connection to the database to
        ///         force a load of the driver while the application current directory
        ///         is temporary changed to a folder where are stored the native dependencies.
        ///     </para>
        /// </summary>
        /// <param name="connectionString"> The connection string. </param>
        /// <returns>An IDbConnection object for the specific Driver.</returns>
        public IDbConnection CreateConnection(string connectionString)
        {
            Check.NotNullOrEmpty(connectionString, nameof(connectionString));

            var cnn = (IDbConnection)Activator.CreateInstance(DbConnectionType);
            string originalCurrentDirectory = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(WorkingDir);
                cnn.ConnectionString = connectionString;
                // hack
                cnn.Open();
                cnn.Close();
            }
            catch { }
            finally
            {
                Directory.SetCurrentDirectory(originalCurrentDirectory);
            }

            return cnn;
        }

        /// <summary>
        ///     Load a DbConnection from a .deps file definition.
        /// </summary>
        protected virtual Type TypeFromAssembly()
        {
            var lib = GetRuntimeLibrary(DriverTypeName.Assembly);

            CopyNativeDepsToWorkingDir(lib);

            string driverPath = GetAssemblyPath(lib);
            var driverAssembly = base.LoadFromAssemblyPath(driverPath);
            return driverAssembly.GetType(DriverTypeName.Type);
        }

        /// <summary>
        ///     <para>
        ///         Called by the <see cref="AssemblyLoadContext"/> when resolving the load of a managed assembly.
        ///     </para>
        ///     <para>
        ///         The same loading strategy is used for the driver.
        ///     </para>
        /// </summary>
        /// <param name="context"> The assembly loader. </param>
        /// <param name="assemblyName"> The name of the assembly. </param>
        /// <returns> The loaded assembly. </returns>
        private Assembly CoreReflectionBasedDriver_Resolving(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            string assemblyPath = GetAssemblyPath(GetRuntimeLibrary(assemblyName.Name));
            return context.LoadFromAssemblyPath(assemblyPath);
        }

        /// <summary>
        ///     Called by the <see cref="AssemblyLoadContext"/> when it does not know how to load the <paramref name="assemblyName"/>.
        ///     Me neither :-) Do nothing.
        /// </summary>
        /// <param name="context"> The assembly loader. </param>
        /// <param name="assemblyName"> The name of the assembly. </param>
        /// <returns> null. </returns>
        protected override Assembly Load(AssemblyName assemblyName) => null;

        /// <summary>
        ///     Copy all the driver's native dependencies to the working directory.
        ///     Do nothing if there is none.
        /// </summary>
        /// <param name="lib"> The driver runtime library. </param>
        protected virtual void CopyNativeDepsToWorkingDir(RuntimeLibrary lib)
        {
            foreach (var dependency in lib.Dependencies)
            {
                var depLib = GetRuntimeLibrary(dependency.Name);
                if (IsLibraryNative(depLib))
                {
                    string source = GetNativeAssemblyPath(depLib);
                    string dest = Path.Combine(WorkingDir, Path.GetFileName(source));
                    if (!File.Exists(dest))
                    {
                        File.Copy(source, dest);
                    }
                }

                CopyNativeDepsToWorkingDir(depLib); // recursive
            }
        }

        private string GetAssemblyPath(RuntimeLibrary lib) => Path.Combine(GetLibraryPackagePath(lib), GetAssemblyRelativePath(lib));

        private string GetNativeAssemblyPath(RuntimeLibrary lib) => Path.Combine(GetLibraryPackagePath(lib), GetNativeAssemblyRelativePath(lib));

        private string GetLibraryPackagePath(RuntimeLibrary lib) => Path.Combine(NugetPackageDir, lib.Path);

        private string GetAssemblyRelativePath(RuntimeLibrary lib) => GetRuntimeAssemblyAssetGroup(lib.Name, lib.RuntimeAssemblyGroups).AssetPaths[0];

        private string GetNativeAssemblyRelativePath(RuntimeLibrary lib) => GetRuntimeAssemblyAssetGroup(lib.Name, lib.NativeLibraryGroups).AssetPaths[0];

        private bool IsLibraryNative(RuntimeLibrary lib) => lib?.NativeLibraryGroups.Count > 0;

        /// <summary>
        ///     <para>
        ///         Given a list of assembly, define the one to target considering the os and architecture the application is running.
        ///     </para>
        ///     <para>
        ///         I am aware of the naivety of the approach, but so far it works for the few database drivers Evolve offers.
        ///     </para>
        /// </summary>
        /// <param name="assemblyName"> Name of the assembly. </param>
        /// <param name="runtimeAssetGroups"> A list of assembly and their RID. </param>
        /// <returns> The relative path to the selected assembly. </returns>
        private RuntimeAssetGroup GetRuntimeAssemblyAssetGroup(string assemblyName, IReadOnlyList<RuntimeAssetGroup> runtimeAssetGroups)
        {
            Check.NotNullOrEmpty(assemblyName, nameof(assemblyName));
            Check.NotNull(runtimeAssetGroups, nameof(runtimeAssetGroups));

            if (runtimeAssetGroups.Count == 0)
            {
                throw new EvolveException(string.Format(NoRuntimeTargetsFound, assemblyName));
            }

            // Only one path, choice is made !
            if (runtimeAssetGroups.Count == 1)
            {
                return runtimeAssetGroups[0];
            }

            // Filter the list by operating system
            var osAssetGroups = runtimeAssetGroups.Where(x => GetOSPlatformRID().Any(s => x.Runtime.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
            if (osAssetGroups.Count == 0)
            {
                throw new EvolveException(string.Format(NoRuntimeTargetsFoundForOS, String.Join(", ", GetOSPlatformRID()), assemblyName));
            }
            if (osAssetGroups.Count == 1)
            {
                return osAssetGroups[0];
            }

            // Filter the list by os and os architecture
            var osArchitectureAssetGroups = osAssetGroups.Where(x => x.Runtime.Contains($"-{RuntimeInformation.OSArchitecture}", StringComparison.OrdinalIgnoreCase)).ToList();
            if (osArchitectureAssetGroups.Count == 0)
            {
                throw new EvolveException(string.Format(NoRuntimeTargetsFoundForOSArchitecture, String.Join(", ", GetOSPlatformRID()),
                                                                                                RuntimeInformation.OSArchitecture,
                                                                                                assemblyName));
            }
            if (osArchitectureAssetGroups.Count == 1)
            {
                return osArchitectureAssetGroups[0];
            }

            // Finally more than one assembly remaining... told u, a real naive implementation
            throw new EvolveException(string.Format(MultipleRuntimeTargetsFound, assemblyName));
        }

        /// <summary>
        ///     Determine the operating system on which the application is running.
        /// </summary>
        /// <returns> The list of compatible os. </returns>
        /// <exception cref="PlatformNotSupportedException"> 
        ///     Throws a PlatformNotSupportedException when the the os is neither based on Windows, Linux or OSX. 
        /// </exception>
        private static IEnumerable<string> GetOSPlatformRID()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new List<string> { "win" };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new List<string> { "linux", "unix" };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new List<string> { "osx", "unix" };
            }

            throw new PlatformNotSupportedException();
        }

        /// <summary>
        ///     Return a list of dependencies, as well as compilation context data and compilation dependencies 
        ///     found in the deps file for a given assembly.
        /// </summary>
        /// <param name="assemblyName"> The name of the assembly to find in the deps file. </param>
        /// <returns></returns>
        /// <exception cref="EvolveException"> Throws an EvolveException when the data of the given assembly can not be loaded. </exception>
        private RuntimeLibrary GetRuntimeLibrary(string assemblyName)
        {
            try
            {
                return ProjectDependencyContext.RuntimeLibraries.Single(x => x.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                throw new EvolveException(string.Format(RuntimeLibraryLoadingError, assemblyName, _depsFile), ex);
            }
        }

        /// <summary>
        ///     <para>
        ///         Creates a folder used as a temp working directory where the 
        ///         driver native dependency assemblies are copied to and loaded from.
        ///     </para>
        ///     <para>
        ///         This folder is created in the user temp directory to avoid permission issues.
        ///     </para>
        /// </summary>
        /// <returns> Path to the working directory. </returns>
        /// <exception cref="EvolveException"> Throws an EvolveException when the creation fails. </exception>
        protected virtual string CreateWorkingDirectory()
        {
            string tempDir = "";
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempDir);
                return tempDir;
            }
            catch (Exception ex)
            {
                throw new EvolveException(string.Format(WorkingDirectoryCreationError, tempDir), ex);
            }
        }

        /// <summary>
        ///     Attempt to return a DbConnection from an already loaded assembly.
        /// </summary>
        /// <returns> A DbConnection type or null. </returns>
        private Type TypeFromLoadedAssembly()
        {
            try
            {
                return Type.GetType(DriverTypeName.ToString());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Load the dependency context of the application to Evolve from a deps.json file.
        /// </summary>
        /// <param name="depsFile"> Path to the deps.json file. </param>
        /// <returns> A dependency context. </returns>
        /// <exception cref="EvolveException"> Throws an EvolveException when the loading fails. </exception>
        private static DependencyContext LoadDependencyContext(string depsFile)
        {
            try
            {
                using (var reader = new DependencyContextJsonReader())
                {
                    using (var stream = File.OpenRead(depsFile))
                    {
                        return reader.Read(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new EvolveException(string.Format(DependencyContextLoadingError, depsFile), ex);
            }
        }

        protected class AssemblyQualifiedTypeName
        {
            public AssemblyQualifiedTypeName(string type, string assembly)
            {
                Type = type;
                Assembly = assembly;
            }

            public string Type { get; }

            public string Assembly { get; }

            public override string ToString()
            {
                if (string.IsNullOrWhiteSpace(Assembly))
                {
                    return Type;
                }

                return string.Concat(Type, ", ", Assembly);
            }
        }
    }
}

#endif
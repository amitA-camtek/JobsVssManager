using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using JobsVssManager.Models;

namespace JobsVssManager.Services
{
    /// <summary>
    /// Native VSS provider using Windows VSS COM API
    /// </summary>
    public class NativeVssProvider : IVssProvider, IDisposable
    {
        private IVssBackupComponents? _backupComponents;
        private readonly Dictionary<string, string> _snapshotDevices = new();
        private Guid _snapshotSetId = Guid.Empty;

        public NativeVssProvider()
        {
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException(
                    "This application requires Administrator privileges to use VSS.\n" +
                    "Please restart Visual Studio or the application as Administrator.");
            }

            InitializeVss();
        }

        private bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void InitializeVss()
        {
            try
            {
                // Load VssApi.dll and get CreateVssBackupComponents function
                IntPtr hModule = NativeMethods.LoadLibrary("VssApi.dll");
                if (hModule == IntPtr.Zero)
                    throw new DllNotFoundException("VssApi.dll not found. VSS may not be installed.");

                IntPtr procAddr = NativeMethods.GetProcAddress(hModule, "CreateVssBackupComponents");
                if (procAddr == IntPtr.Zero)
                {
                    NativeMethods.FreeLibrary(hModule);
                    throw new EntryPointNotFoundException("CreateVssBackupComponents function not found in VssApi.dll");
                }

                // Create delegate for CreateVssBackupComponents
                var createFunc = Marshal.GetDelegateForFunctionPointer<CreateVssBackupComponentsDelegate>(procAddr);

                // Call CreateVssBackupComponents
                int hr = createFunc(out _backupComponents);
                NativeMethods.FreeLibrary(hModule);

                if (hr != 0 || _backupComponents == null)
                    throw new COMException($"CreateVssBackupComponents failed. HRESULT: 0x{hr:X8}", hr);

                // Initialize for backup
                hr = _backupComponents.InitializeForBackup(IntPtr.Zero);
                if (hr != 0)
                    throw new COMException($"InitializeForBackup failed. HRESULT: 0x{hr:X8}", hr);

                // Set backup state
                hr = _backupComponents.SetBackupState(
                    false, // selectComponents
                    true,  // backupBootableSystemState
                    VssBackupType.VSS_BT_COPY,
                    false); // partialFileSupport

                if (hr != 0)
                    throw new COMException($"SetBackupState failed. HRESULT: 0x{hr:X8}", hr);

                // Set context for client-accessible snapshots
                hr = _backupComponents.SetContext(VssSnapshotContext.VSS_CTX_BACKUP);
                if (hr != 0)
                    throw new COMException($"SetContext failed. HRESULT: 0x{hr:X8}", hr);

                // Gather writer metadata
                hr = _backupComponents.GatherWriterMetadata(out IntPtr asyncPtr);
                if (hr != 0)
                    throw new COMException($"GatherWriterMetadata failed. HRESULT: 0x{hr:X8}", hr);

                if (asyncPtr != IntPtr.Zero)
                {
                    var async = (IVssAsync)Marshal.GetObjectForIUnknown(asyncPtr);
                    try
                    {
                        WaitForAsync(async);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(async);
                        Marshal.Release(asyncPtr);
                    }
                }
            }
            catch
            {
                if (_backupComponents != null)
                {
                    Marshal.ReleaseComObject(_backupComponents);
                    _backupComponents = null;
                }
                throw;
            }
        }

        public SnapshotModel CreateSnapshot(string volume, string description)
        {
            if (_backupComponents == null)
                throw new InvalidOperationException("VSS not initialized");

            try
            {
                if (!volume.EndsWith("\\", StringComparison.Ordinal))
                    volume += "\\";

                // Start snapshot set
                var hr = _backupComponents.StartSnapshotSet(out _snapshotSetId);
                if (hr != 0)
                    throw new COMException($"StartSnapshotSet failed. HRESULT: 0x{hr:X8}", hr);

                // Add volume to snapshot set
                hr = _backupComponents.AddToSnapshotSet(volume, Guid.Empty, out var snapshotId);
                if (hr != 0)
                    throw new COMException($"AddToSnapshotSet failed. HRESULT: 0x{hr:X8}", hr);

                // Prepare for backup
                hr = _backupComponents.PrepareForBackup(out var asyncPreparePtr);
                if (hr != 0)
                    throw new COMException($"PrepareForBackup failed. HRESULT: 0x{hr:X8}", hr);

                var asyncPrepare = (IVssAsync)Marshal.GetObjectForIUnknown(asyncPreparePtr);
                try
                {
                    WaitForAsync(asyncPrepare);
                }
                finally
                {
                    Marshal.ReleaseComObject(asyncPrepare);
                    Marshal.Release(asyncPreparePtr);
                }

                // Create the snapshot
                hr = _backupComponents.DoSnapshotSet(out var asyncSnapshotPtr);
                if (hr != 0)
                    throw new COMException($"DoSnapshotSet failed. HRESULT: 0x{hr:X8}", hr);

                var asyncSnapshot = (IVssAsync)Marshal.GetObjectForIUnknown(asyncSnapshotPtr);
                try
                {
                    WaitForAsync(asyncSnapshot);
                }
                finally
                {
                    Marshal.ReleaseComObject(asyncSnapshot);
                    Marshal.Release(asyncSnapshotPtr);
                }

                // Get snapshot properties
                hr = _backupComponents.GetSnapshotProperties(snapshotId, out var props);
                if (hr != 0)
                    throw new COMException($"GetSnapshotProperties failed. HRESULT: 0x{hr:X8}", hr);

                _snapshotDevices[snapshotId.ToString("B")] = props.m_pwszSnapshotDeviceObject;

                var snapshot = new SnapshotModel
                {
                    Id = snapshotId.ToString("B"),
                    Volume = volume,
                    CreatedAt = DateTime.FromFileTime(props.m_tsCreationTimestamp),
                    Description = description
                };

                VssApi.VssFreeSnapshotProperties(ref props);
                return snapshot;
            }
            catch
            {
                if (_snapshotSetId != Guid.Empty)
                {
                    try
                    {
                        _backupComponents?.DeleteSnapshots(_snapshotSetId,
                            VssObjectType.VSS_OBJECT_SNAPSHOT_SET, true, out _, out _);
                    }
                    catch { }
                }
                throw;
            }
        }

        public IEnumerable<SnapshotModel> ListSnapshots(string volume)
        {
            if (_backupComponents == null)
                throw new InvalidOperationException("VSS not initialized");

            var snapshots = new List<SnapshotModel>();

            try
            {
                var hr = _backupComponents.Query(Guid.Empty, VssObjectType.VSS_OBJECT_NONE,
                    VssObjectType.VSS_OBJECT_SNAPSHOT, out var enumPtr);

                if (hr != 0 || enumPtr == IntPtr.Zero)
                    return snapshots;

                var enumSnapshots = (IVssEnumObject)Marshal.GetObjectForIUnknown(enumPtr);

                while (true)
                {
                    hr = enumSnapshots.Next(1, out var prop, out var fetched);
                    if (hr != 0 || fetched == 0)
                        break;

                    if (prop.Type == VssObjectType.VSS_OBJECT_SNAPSHOT)
                    {
                        var snapshot = prop.Obj.Snapshot;
                        if (snapshot.m_pwszOriginalVolumeName.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
                        {
                            var id = snapshot.m_SnapshotId.ToString("B");
                            _snapshotDevices[id] = snapshot.m_pwszSnapshotDeviceObject;

                            snapshots.Add(new SnapshotModel
                            {
                                Id = id,
                                Volume = snapshot.m_pwszOriginalVolumeName,
                                CreatedAt = DateTime.FromFileTime(snapshot.m_tsCreationTimestamp),
                                Description = "VSS Snapshot"
                            });
                        }
                    }

                    VssApi.VssFreeSnapshotProperties(ref prop.Obj.Snapshot);
                }

                Marshal.ReleaseComObject(enumSnapshots);
                Marshal.Release(enumPtr);
            }
            catch { }

            return snapshots;
        }

        public void DeleteSnapshot(string snapshotId)
        {
            if (_backupComponents == null)
                throw new InvalidOperationException("VSS not initialized");

            var guid = new Guid(snapshotId);
            var hr = _backupComponents.DeleteSnapshots(guid, VssObjectType.VSS_OBJECT_SNAPSHOT,
                true, out _, out _);

            if (hr != 0)
                throw new COMException($"DeleteSnapshots failed. HRESULT: 0x{hr:X8}", hr);

            _snapshotDevices.Remove(snapshotId);
        }

        public string GetSnapshotPath(string snapshotId, string volume)
        {
            if (_snapshotDevices.TryGetValue(snapshotId, out var devicePath))
                return devicePath.TrimEnd('\\') + "\\";

            var snapshots = ListSnapshots(volume);
            var snapshot = snapshots.FirstOrDefault(s => s.Id == snapshotId);

            if (snapshot != null && _snapshotDevices.TryGetValue(snapshotId, out devicePath))
                return devicePath.TrimEnd('\\') + "\\";

            throw new Exception($"Snapshot device path not found for ID: {snapshotId}");
        }

        private void WaitForAsync(IVssAsync async)
        {
            var hr = async.Wait(0xFFFFFFFF);
            if (hr != 0)
            {
                async.QueryStatus(out var asyncHr, out _);
                throw new COMException($"VSS async operation failed. HRESULT: 0x{asyncHr:X8}", asyncHr);
            }
        }

        public void Dispose()
        {
            if (_backupComponents != null)
            {
                try
                {
                    _backupComponents.BackupComplete(out var asyncPtr);
                    if (asyncPtr != IntPtr.Zero)
                    {
                        var async = (IVssAsync)Marshal.GetObjectForIUnknown(asyncPtr);
                        try { WaitForAsync(async); } catch { }
                        Marshal.ReleaseComObject(async);
                        Marshal.Release(asyncPtr);
                    }
                }
                catch { }

                Marshal.ReleaseComObject(_backupComponents);
                _backupComponents = null;
            }
        }
    }

    #region COM Interop

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateVssBackupComponentsDelegate(
        [MarshalAs(UnmanagedType.Interface)] out IVssBackupComponents? ppBackup);

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr hModule);
    }

    internal static class VssApi
    {
        [DllImport("VssApi.dll", CharSet = CharSet.Unicode)]
        public static extern void VssFreeSnapshotProperties(ref VssSnapshotProp pProp);
    }

    [ComImport]
    [Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssBackupComponents
    {
        [PreserveSig] int GetWriterComponentsCount(out uint count);
        [PreserveSig] int GetWriterComponents(uint index, out IntPtr components);
        [PreserveSig] int InitializeForBackup(IntPtr bstrXml);
        [PreserveSig]
        int SetBackupState(bool selectComponents, bool backupBootableSystemState,
            VssBackupType backupType, bool partialFileSupport);
        [PreserveSig] int InitializeForRestore(IntPtr bstrXml);
        [PreserveSig] int SetRestoreState(VssRestoreType restoreType);
        [PreserveSig] int GatherWriterMetadata(out IntPtr pAsync);
        [PreserveSig] int GetWriterMetadataCount(out uint count);
        [PreserveSig] int GetWriterMetadata(uint index, out Guid instanceId, out IntPtr metadata);
        [PreserveSig] int FreeWriterMetadata();
        [PreserveSig]
        int AddComponent(Guid instanceId, Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName);
        [PreserveSig] int PrepareForBackup(out IntPtr ppAsync);
        [PreserveSig] int AbortBackup();
        [PreserveSig] int GatherWriterStatus(out IntPtr pAsync);
        [PreserveSig] int GetWriterStatusCount(out uint count);
        [PreserveSig] int FreeWriterStatus();
        [PreserveSig]
        int GetWriterStatus(uint index, out Guid instanceId, out Guid writerId,
            out IntPtr bstrWriter, out VssWriterState status, out int hResultFailure);
        [PreserveSig]
        int SetBackupSucceeded(Guid instanceId, Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, bool succeeded);
        [PreserveSig]
        int SetBackupOptions(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszBackupOptions);
        [PreserveSig]
        int SetSelectedForRestore(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, bool selectedForRestore);
        [PreserveSig]
        int SetRestoreOptions(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszRestoreOptions);
        [PreserveSig]
        int SetAdditionalRestores(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, bool additionalRestores);
        [PreserveSig]
        int SetPreviousBackupStamp(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszPreviousBackupStamp);
        [PreserveSig] int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string pbstrXML);
        [PreserveSig] int BackupComplete(out IntPtr ppAsync);
        [PreserveSig]
        int AddAlternativeLocationMapping(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszFilespec, bool recursive,
            [MarshalAs(UnmanagedType.LPWStr)] string wszDestination);
        [PreserveSig]
        int AddRestoreSubcomponent(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentName, bool repair);
        [PreserveSig]
        int SetFileRestoreStatus(Guid writerId, VssComponentType componentType,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, VssFileRestoreStatus status);
        [PreserveSig]
        int AddNewTarget(Guid writerId, VssComponentType ct,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName,
            [MarshalAs(UnmanagedType.LPWStr)] string wszPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszFileName, bool recursive,
            [MarshalAs(UnmanagedType.LPWStr)] string wszAlternatePath);
        [PreserveSig]
        int SetRangesFilePath(Guid writerId, VssComponentType ct,
            [MarshalAs(UnmanagedType.LPWStr)] string wszLogicalPath,
            [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, uint iPartialFile,
            [MarshalAs(UnmanagedType.LPWStr)] string wszRangesFile);
        [PreserveSig] int PreRestore(out IntPtr ppAsync);
        [PreserveSig] int PostRestore(out IntPtr ppAsync);
        [PreserveSig] int SetContext(VssSnapshotContext context);
        [PreserveSig] int StartSnapshotSet(out Guid snapshotSetId);
        [PreserveSig]
        int AddToSnapshotSet([MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName,
            Guid providerId, out Guid snapshotId);
        [PreserveSig] int DoSnapshotSet(out IntPtr ppAsync);
        [PreserveSig]
        int DeleteSnapshots(Guid sourceObjectId, VssObjectType objectType,
            bool forceDelete, out int deletedSnapshots, out Guid nondeletedSnapshotID);
        [PreserveSig] int ImportSnapshots();
        [PreserveSig] int BreakSnapshotSet(Guid snapshotSetId);
        [PreserveSig] int GetSnapshotProperties(Guid snapshotId, out VssSnapshotProp properties);
        [PreserveSig]
        int Query(Guid queriedObjectId, VssObjectType queriedObjectType,
            VssObjectType returnedObjectsType, out IntPtr ppEnum);
        [PreserveSig]
        int IsVolumeSupported(Guid providerId,
            [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName, out bool supported);
        [PreserveSig] int DisableWriterClasses(ref Guid rgWriterClassId, uint cClassId);
        [PreserveSig] int EnableWriterClasses(ref Guid rgWriterClassId, uint cClassId);
        [PreserveSig] int DisableWriterInstances(ref Guid rgWriterInstanceId, uint cInstanceId);
        [PreserveSig]
        int ExposeSnapshot(Guid snapshotId,
            [MarshalAs(UnmanagedType.LPWStr)] string wszPathFromRoot,
            VssVolumeSnapshotAttributes attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string wszExpose, out IntPtr pwszExposed);
        [PreserveSig] int RevertToSnapshot(Guid snapshotId, bool forceDismount);
        [PreserveSig]
        int QueryRevertStatus([MarshalAs(UnmanagedType.LPWStr)] string pwszVolume,
            out IntPtr ppAsync);
    }

    [ComImport]
    [Guid("507C37B4-CF5B-4e95-B0AF-14EB9767467E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssAsync
    {
        [PreserveSig] int Cancel();
        [PreserveSig] int Wait(uint dwMilliseconds);
        [PreserveSig] int QueryStatus(out int pHrResult, out int pReserved);
    }

    [ComImport]
    [Guid("AE1C7110-2F60-11d3-8A39-00C04F72D8E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVssEnumObject
    {
        [PreserveSig] int Next(uint celt, out VssObjectProp rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IVssEnumObject ppenum);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VssObjectProp
    {
        public VssObjectType Type;
        public VssObjectUnion Obj;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct VssObjectUnion
    {
        [FieldOffset(0)] public VssSnapshotProp Snapshot;
        [FieldOffset(0)] public VssProviderProp Provider;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct VssSnapshotProp
    {
        public Guid m_SnapshotId;
        public Guid m_SnapshotSetId;
        public int m_lSnapshotsCount;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszSnapshotDeviceObject;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszOriginalVolumeName;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszOriginatingMachine;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszServiceMachine;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszExposedName;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszExposedPath;
        public Guid m_ProviderId;
        public VssVolumeSnapshotAttributes m_lSnapshotAttributes;
        public long m_tsCreationTimestamp;
        public VssSnapshotState m_eStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VssProviderProp
    {
        public Guid m_ProviderId;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszProviderName;
        public VssProviderType m_eProviderType;
        [MarshalAs(UnmanagedType.LPWStr)] public string m_pwszProviderVersion;
        public Guid m_ProviderVersionId;
        public Guid m_ClassId;
    }

    internal enum VssBackupType
    {
        VSS_BT_UNDEFINED = 0, VSS_BT_FULL = 1, VSS_BT_INCREMENTAL = 2,
        VSS_BT_DIFFERENTIAL = 3, VSS_BT_LOG = 4, VSS_BT_COPY = 5, VSS_BT_OTHER = 6
    }
    internal enum VssRestoreType
    {
        VSS_RTYPE_UNDEFINED = 0, VSS_RTYPE_BY_COPY = 1,
        VSS_RTYPE_IMPORT = 2, VSS_RTYPE_OTHER = 3
    }
    internal enum VssObjectType
    {
        VSS_OBJECT_UNKNOWN = 0, VSS_OBJECT_NONE = 1,
        VSS_OBJECT_SNAPSHOT_SET = 2, VSS_OBJECT_SNAPSHOT = 3, VSS_OBJECT_PROVIDER = 4,
        VSS_OBJECT_TYPE_COUNT = 5
    }
    internal enum VssComponentType { VSS_CT_UNDEFINED = 0, VSS_CT_DATABASE = 1, VSS_CT_FILEGROUP = 2 }
    internal enum VssWriterState
    {
        VSS_WS_UNKNOWN = 0, VSS_WS_STABLE = 1, VSS_WS_WAITING_FOR_FREEZE = 2,
        VSS_WS_WAITING_FOR_THAW = 3, VSS_WS_WAITING_FOR_POST_SNAPSHOT = 4,
        VSS_WS_WAITING_FOR_BACKUP_COMPLETE = 5, VSS_WS_FAILED_AT_IDENTIFY = 6,
        VSS_WS_FAILED_AT_PREPARE_BACKUP = 7, VSS_WS_FAILED_AT_PREPARE_SNAPSHOT = 8,
        VSS_WS_FAILED_AT_FREEZE = 9, VSS_WS_FAILED_AT_THAW = 10, VSS_WS_FAILED_AT_POST_SNAPSHOT = 11,
        VSS_WS_FAILED_AT_BACKUP_COMPLETE = 12, VSS_WS_FAILED_AT_PRE_RESTORE = 13,
        VSS_WS_FAILED_AT_POST_RESTORE = 14, VSS_WS_FAILED_AT_BACKUPSHUTDOWN = 15
    }
    internal enum VssFileRestoreStatus { VSS_RS_UNDEFINED = 0, VSS_RS_NONE = 1, VSS_RS_ALL = 2, VSS_RS_FAILED = 3 }

    [Flags]
    internal enum VssVolumeSnapshotAttributes : uint
    {
        VSS_VOLSNAP_ATTR_PERSISTENT = 0x00000001,
        VSS_VOLSNAP_ATTR_NO_AUTORECOVERY = 0x00000002,
        VSS_VOLSNAP_ATTR_CLIENT_ACCESSIBLE = 0x00000004,
        VSS_VOLSNAP_ATTR_NO_AUTO_RELEASE = 0x00000008,
        VSS_VOLSNAP_ATTR_NO_WRITERS = 0x00000010,
        VSS_VOLSNAP_ATTR_TRANSPORTABLE = 0x00000020
    }

    internal enum VssSnapshotState
    {
        VSS_SS_UNKNOWN = 0, VSS_SS_PREPARING = 1, VSS_SS_PROCESSING_PREPARE = 2,
        VSS_SS_PREPARED = 3, VSS_SS_PROCESSING_PRECOMMIT = 4, VSS_SS_PRECOMMITTED = 5,
        VSS_SS_PROCESSING_COMMIT = 6, VSS_SS_COMMITTED = 7, VSS_SS_PROCESSING_POSTCOMMIT = 8,
        VSS_SS_PROCESSING_PREFINALCOMMIT = 9, VSS_SS_PREFINALCOMMITTED = 10,
        VSS_SS_PROCESSING_POSTFINALCOMMIT = 11, VSS_SS_CREATED = 12, VSS_SS_ABORTED = 13,
        VSS_SS_DELETED = 14, VSS_SS_POSTCOMMITTED = 15
    }

    internal enum VssProviderType
    {
        VSS_PROV_UNKNOWN = 0, VSS_PROV_SYSTEM = 1, VSS_PROV_SOFTWARE = 2,
        VSS_PROV_HARDWARE = 3, VSS_PROV_FILESHARE = 4
    }

    internal enum VssSnapshotContext : uint
    {
        VSS_CTX_BACKUP = 0, VSS_CTX_FILE_SHARE_BACKUP = 0x00000010,
        VSS_CTX_NAS_ROLLBACK = 0x00000019, VSS_CTX_APP_ROLLBACK = 0x00000009,
        VSS_CTX_CLIENT_ACCESSIBLE = 0x0000001d, VSS_CTX_CLIENT_ACCESSIBLE_WRITERS = 0x0000000d,
        VSS_CTX_ALL = 0xffffffff
    }

    #endregion
}
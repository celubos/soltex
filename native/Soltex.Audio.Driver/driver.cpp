// Original Soltex ACX endpoint/PCM transport implementation. No sample tone/file engines.
#include <ntddk.h>
#include <wdf.h>
#include <windef.h>
#include <initguid.h>
#include <ks.h>
#include <mmsystem.h>
#include <ksmedia.h>
#include <acx.h>
#include <wdmsec.h>
#include "transport.h"

constexpr ULONG Tag = 'xtlS';
struct BUS { KSPIN_LOCK Lock; ST_RING Ring; ULONGLONG Generation; LONG Active, Running; };
struct DEVICE { ACXCIRCUIT Circuits[ST_BUS_COUNT]; BUS Buses[ST_BUS_COUNT]; };
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE, DeviceContext);
struct CIRCUIT { DEVICE* Device; ULONG Bus; };
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(CIRCUIT, CircuitContext);
struct STREAM {
    BUS* Bus; ACXSTREAM Handle; WDFTIMER Timer; PACX_RTPACKET Packets;
    ULONG Count, Size, Current, StartPacket, Ready[2], ValidBytes[2];
    ULONGLONG StartTime, CurrentQpc, LastQpc, PresentedFrames; BOOLEAN Capture, Running, EndOfStream, ReadyValid[2];
    KSPIN_LOCK Lock;
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(STREAM, StreamContext);
static FAST_MUTEX GlobalLock;
static DEVICE* CurrentDevice;
static WDFDEVICE ControlDevice;
static const GUID ComponentBase = {0x38e73110,0xd64b,0x49ee,{0xb5,0x24,0xb0,0x16,0xda,0x70,0x1f,0x10}};
EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
EVT_WDF_DRIVER_UNLOAD DriverUnload;
EVT_WDF_OBJECT_CONTEXT_CLEANUP DeviceCleanup;
EVT_WDF_DEVICE_PREPARE_HARDWARE PrepareDevice;
EVT_WDF_DEVICE_RELEASE_HARDWARE ReleaseDevice;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL Control;
EVT_WDF_FILE_CLEANUP LeaseCleanup;
EVT_ACX_CIRCUIT_CREATE_STREAM CreateStream;
EVT_ACX_STREAM_ALLOCATE_RTPACKETS AllocatePackets;
EVT_ACX_STREAM_FREE_RTPACKETS FreePackets;
EVT_ACX_STREAM_PREPARE_HARDWARE PrepareStream;
EVT_ACX_STREAM_RELEASE_HARDWARE ReleaseStream;
EVT_ACX_STREAM_RUN RunStream;
EVT_ACX_STREAM_PAUSE PauseStream;
EVT_ACX_STREAM_GET_HW_LATENCY Latency;
EVT_ACX_STREAM_GET_CURRENT_PACKET CurrentPacket;
EVT_ACX_STREAM_GET_PRESENTATION_POSITION Position;
EVT_ACX_STREAM_SET_RENDER_PACKET SetRender;
EVT_ACX_STREAM_GET_CAPTURE_PACKET GetCapture;
EVT_WDF_TIMER PacketTimer;
EVT_WDF_OBJECT_CONTEXT_DESTROY DestroyStream;

static void ResetBus(BUS* bus) {
    KIRQL irql; KeAcquireSpinLock(&bus->Lock, &irql);
    StReset(&bus->Ring); ++bus->Generation;
    KeReleaseSpinLock(&bus->Lock, irql);
}

void LeaseCleanup(WDFFILEOBJECT) {
    ExAcquireFastMutex(&GlobalLock);
    if (CurrentDevice) for (auto& b : CurrentDevice->Buses) ResetBus(&b);
    ExReleaseFastMutex(&GlobalLock);
}

void Control(WDFQUEUE, WDFREQUEST request, size_t outLength, size_t inLength, ULONG code) {
    ST_REQUEST* input = nullptr; void* output = nullptr;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(ST_REQUEST), reinterpret_cast<void**>(&input), nullptr);
    if (!NT_SUCCESS(status)) { WdfRequestComplete(request, status); return; }
    ST_REQUEST q = *input; // METHOD_BUFFERED aliases input/output: copy before overwriting.
    if (!StValid(q) || (code != ST_IOCTL_QUERY && code != ST_IOCTL_READ && code != ST_IOCTL_WRITE) ||
        (code == ST_IOCTL_READ && q.Bus >= ST_MIC) || (code == ST_IOCTL_WRITE && q.Bus < ST_MIC) ||
        (code != ST_IOCTL_WRITE && (q.Generation != 0 || q.Sequence != 0)) || (code == ST_IOCTL_QUERY && q.Frames != 0) ||
        inLength != sizeof(q) + (code == ST_IOCTL_WRITE ? q.Frames * ST_FRAME_BYTES : 0)) {
        WdfRequestComplete(request, STATUS_INVALID_PARAMETER); return;
    }
    const size_t needed = sizeof(ST_RESPONSE) + (code == ST_IOCTL_READ ? q.Frames * ST_FRAME_BYTES : 0);
    if (outLength < needed) { WdfRequestComplete(request, STATUS_BUFFER_TOO_SMALL); return; }
    status = WdfRequestRetrieveOutputBuffer(request, needed, &output, nullptr);
    if (!NT_SUCCESS(status)) { WdfRequestComplete(request, status); return; }
    ExAcquireFastMutex(&GlobalLock);
    if (!CurrentDevice) { ExReleaseFastMutex(&GlobalLock); WdfRequestComplete(request, STATUS_DEVICE_NOT_READY); return; }
    BUS* b = &CurrentDevice->Buses[q.Bus];
    KIRQL irql; KeAcquireSpinLock(&b->Lock, &irql);
    ST_U32 frames = 0;
    if (code == ST_IOCTL_READ) frames = StPop(&b->Ring, static_cast<unsigned char*>(output) + sizeof(ST_RESPONSE), q.Frames, false);
    else if (code == ST_IOCTL_WRITE) {
        // Reject stale producers; sequence counts submitted frames within this lease generation.
        if (!StInject(&b->Ring, reinterpret_cast<unsigned char*>(input) + sizeof(q), q.Frames, q.Sequence, q.Generation, b->Generation)) status = STATUS_REVISION_MISMATCH;
        else frames = q.Frames;
    }
    ST_RESPONSE response = { ST_VERSION, sizeof(ST_RESPONSE), q.Bus, frames, b->Ring.Produced,
        b->Generation, b->Ring.Dropped, b->Ring.Underrun, static_cast<ST_U32>(b->Running != 0), b->Ring.Count };
    KeReleaseSpinLock(&b->Lock, irql);
    ExReleaseFastMutex(&GlobalLock);
    if (NT_SUCCESS(status)) { *static_cast<ST_RESPONSE*>(output) = response; WdfRequestCompleteWithInformation(request, status, sizeof(response) + (code == ST_IOCTL_READ ? frames * ST_FRAME_BYTES : 0)); }
    else WdfRequestComplete(request, status);
}

void FreePackets(ACXSTREAM stream, PACX_RTPACKET packets, ULONG count) {
    auto s = StreamContext(stream);
    if (s->Timer) WdfTimerStop(s->Timer, TRUE);
    for (ULONG i = 0; i < count; ++i) if (packets[i].RtPacketBuffer.u.MdlType.Mdl) {
        auto mdl = packets[i].RtPacketBuffer.u.MdlType.Mdl;
        auto memory = MmGetMdlVirtualAddress(mdl); IoFreeMdl(mdl); ExFreePoolWithTag(memory, Tag);
    }
    ExFreePoolWithTag(packets, Tag); s->Packets = nullptr; s->Count = s->Size = 0;
}

NTSTATUS AllocatePackets(ACXSTREAM stream, ULONG count, ULONG size, PACX_RTPACKET* result) {
    auto s = StreamContext(stream); *result = nullptr;
    if (s->Packets || count != 2 || size < 192 || size > ST_MAX_FRAMES * ST_FRAME_BYTES || size % ST_FRAME_BYTES) return STATUS_INVALID_PARAMETER;
    auto packets = static_cast<PACX_RTPACKET>(ExAllocatePool2(POOL_FLAG_NON_PAGED, count * sizeof(ACX_RTPACKET), Tag));
    if (!packets) return STATUS_INSUFFICIENT_RESOURCES;
    const ULONG allocated = (size + PAGE_SIZE - 1) & ~(PAGE_SIZE - 1);
    for (ULONG i = 0; i < count; ++i) {
        ACX_RTPACKET_INIT(&packets[i]);
        auto buffer = ExAllocatePool2(POOL_FLAG_NON_PAGED, allocated, Tag);
        if (!buffer) { FreePackets(stream, packets, count); return STATUS_INSUFFICIENT_RESOURCES; }
        auto mdl = IoAllocateMdl(buffer, allocated, FALSE, FALSE, nullptr);
        if (!mdl) { ExFreePoolWithTag(buffer, Tag); FreePackets(stream, packets, count); return STATUS_INSUFFICIENT_RESOURCES; }
        MmBuildMdlForNonPagedPool(mdl);
        WDF_MEMORY_DESCRIPTOR_INIT_MDL(&packets[i].RtPacketBuffer, mdl, allocated);
        packets[i].RtPacketSize = size;
        packets[i].RtPacketOffset = i == 0 ? allocated - size : 0;
    }
    s->Packets = packets; s->Count = count; s->Size = size; *result = packets; return STATUS_SUCCESS;
}

static void Schedule(STREAM* s) {
    const ULONGLONG elapsed = (static_cast<ULONGLONG>(s->Current - s->StartPacket) + 1) * s->Size * 10000000 / (ST_RATE * ST_FRAME_BYTES);
    const ULONGLONG now = KeQueryInterruptTime();
    const ULONGLONG due = s->StartTime + elapsed;
    // No recursive catch-up on a stalled CPU. Rebase once instead of flooding callbacks.
    if (due <= now) { s->StartTime += now - due + 10000; WdfTimerStart(s->Timer, -10000); }
    else WdfTimerStart(s->Timer, -static_cast<LONGLONG>(due - now));
}

void PacketTimer(WDFTIMER timer) {
    ACXSTREAM stream = reinterpret_cast<ACXSTREAM>(WdfTimerGetParentObject(timer));
    auto s = StreamContext(stream);
    KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql);
    if (!s->Running || !s->Packets) { KeReleaseSpinLock(&s->Lock, irql); return; }
    const ULONG packet = s->Current, index = packet % s->Count;
    auto& p = s->Packets[index];
    auto data = static_cast<unsigned char*>(MmGetMdlVirtualAddress(p.RtPacketBuffer.u.MdlType.Mdl)) + p.RtPacketOffset;
    KeAcquireSpinLockAtDpcLevel(&s->Bus->Lock);
    if (s->Capture) StPop(&s->Bus->Ring, data, s->Size / ST_FRAME_BYTES, true);
    else {
        if (!s->ReadyValid[index] || s->Ready[index] != packet) { RtlZeroMemory(data, s->Size); s->Bus->Ring.Underrun += s->Size / ST_FRAME_BYTES; }
        else if (s->ValidBytes[index] < s->Size) RtlZeroMemory(data + s->ValidBytes[index], s->Size - s->ValidBytes[index]);
        StPush(&s->Bus->Ring, data, s->Size / ST_FRAME_BYTES);
        s->ReadyValid[index] = FALSE;
    }
    KeReleaseSpinLockFromDpcLevel(&s->Bus->Lock);
    ++s->Current; s->PresentedFrames += s->Size / ST_FRAME_BYTES; s->LastQpc = s->CurrentQpc; s->CurrentQpc = KeQueryPerformanceCounter(nullptr).QuadPart;
    const ULONGLONG qpc = s->CurrentQpc;
    Schedule(s); KeReleaseSpinLock(&s->Lock, irql);
    AcxRtStreamNotifyPacketComplete(stream, packet, qpc);
}

NTSTATUS PrepareStream(ACXSTREAM stream) {
    auto s = StreamContext(stream);
    if (s->Timer) return STATUS_SUCCESS;
    WDF_TIMER_CONFIG config; WDF_TIMER_CONFIG_INIT(&config, PacketTimer); config.UseHighResolutionTimer = WdfTrue;
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ParentObject = stream;
    attributes.ExecutionLevel = WdfExecutionLevelDispatch; attributes.SynchronizationScope = WdfSynchronizationScopeNone;
    return WdfTimerCreate(&config, &attributes, &s->Timer);
}
NTSTATUS PauseStream(ACXSTREAM stream) {
    auto s = StreamContext(stream); KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql); s->Running = FALSE; InterlockedExchange(&s->Bus->Running, 0); KeReleaseSpinLock(&s->Lock, irql);
    if (s->Timer) WdfTimerStop(s->Timer, TRUE); return STATUS_SUCCESS;
}
NTSTATUS ReleaseStream(ACXSTREAM stream) {
    PauseStream(stream); auto s = StreamContext(stream);
    if (s->Timer) { WdfObjectDelete(s->Timer); s->Timer = nullptr; }
    s->Current = s->StartPacket = 0; s->PresentedFrames = 0; s->ReadyValid[0] = s->ReadyValid[1] = FALSE; s->EndOfStream = FALSE; ResetBus(s->Bus); return STATUS_SUCCESS;
}
NTSTATUS RunStream(ACXSTREAM stream) {
    auto s = StreamContext(stream); if (!s->Timer || !s->Packets) return STATUS_INVALID_DEVICE_STATE;
    KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql);
    if (!s->Running) { s->Running = TRUE; InterlockedExchange(&s->Bus->Running, 1); s->StartPacket = s->Current; s->StartTime = KeQueryInterruptTime(); s->CurrentQpc = KeQueryPerformanceCounter(nullptr).QuadPart; Schedule(s); }
    KeReleaseSpinLock(&s->Lock, irql); return STATUS_SUCCESS;
}
NTSTATUS Latency(ACXSTREAM, ULONG* fifo, ULONG* delay) { *fifo = *delay = 0; return STATUS_SUCCESS; }
NTSTATUS CurrentPacket(ACXSTREAM stream, ULONG* packet) {
    auto s = StreamContext(stream); KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql); *packet = s->Current; KeReleaseSpinLock(&s->Lock, irql); return STATUS_SUCCESS;
}
NTSTATUS Position(ACXSTREAM stream, ULONGLONG* blocks, ULONGLONG* qpc) {
    auto s = StreamContext(stream); KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql);
    *blocks = s->PresentedFrames; *qpc = KeQueryPerformanceCounter(nullptr).QuadPart;
    KeReleaseSpinLock(&s->Lock, irql); return STATUS_SUCCESS;
}
NTSTATUS SetRender(ACXSTREAM stream, ULONG packet, ULONG flags, ULONG eos) {
    auto s = StreamContext(stream); KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql);
    NTSTATUS status = STATUS_SUCCESS;
    const auto check = StCheckPacket(packet, s->Current, s->Count, s->Running != FALSE, s->EndOfStream != FALSE, flags, eos, s->Size);
    if (check == StPacketState) status = STATUS_INVALID_DEVICE_STATE;
    else if (check == StPacketLate) status = STATUS_DATA_LATE_ERROR;
    else if (check == StPacketOverrun) status = STATUS_DATA_OVERRUN;
    else if (check == StPacketInvalid) status = STATUS_INVALID_PARAMETER;
    else {
        auto index = packet % s->Count; s->Ready[index] = packet; s->ReadyValid[index] = TRUE;
        s->EndOfStream = (flags & KSSTREAM_HEADER_OPTIONSF_ENDOFSTREAM) != 0;
        s->ValidBytes[index] = s->EndOfStream ? eos : s->Size;
    }
    KeReleaseSpinLock(&s->Lock, irql); return status;
}
NTSTATUS GetCapture(ACXSTREAM stream, ULONG* packet, ULONGLONG* qpc, BOOLEAN* more) {
    auto s = StreamContext(stream); KIRQL irql; KeAcquireSpinLock(&s->Lock, &irql);
    *packet = s->Current - 1; *qpc = s->LastQpc; *more = FALSE;
    NTSTATUS result = s->Current ? STATUS_SUCCESS : STATUS_DEVICE_NOT_READY;
    KeReleaseSpinLock(&s->Lock, irql); return result;
}
void DestroyStream(WDFOBJECT object) {
    auto s = StreamContext(object);
    if (s->Bus) { ResetBus(s->Bus); InterlockedExchange(&s->Bus->Running, 0); InterlockedExchange(&s->Bus->Active, 0); }
}

NTSTATUS CreateStream(WDFDEVICE device, ACXCIRCUIT circuit, ACXPIN, PACXSTREAM_INIT init,
    ACXDATAFORMAT format, const GUID*, ACXOBJECTBAG) {
    auto c = CircuitContext(circuit); auto b = &c->Device->Buses[c->Bus];
    auto wave = static_cast<PWAVEFORMATEXTENSIBLE>(AcxDataFormatGetWaveFormatExtensible(format));
    if (!wave || wave->Format.nSamplesPerSec != ST_RATE || wave->Format.nChannels != ST_CHANNELS || wave->Format.wBitsPerSample != 16 || wave->Format.nBlockAlign != ST_FRAME_BYTES || wave->Samples.wValidBitsPerSample != 16 || !IsEqualGUID(wave->SubFormat, KSDATAFORMAT_SUBTYPE_PCM)) return STATUS_NO_MATCH;
    if (InterlockedCompareExchange(&b->Active, 1, 0)) return STATUS_DEVICE_BUSY;
    ACX_STREAM_CALLBACKS callbacks; ACX_STREAM_CALLBACKS_INIT(&callbacks);
    callbacks.EvtAcxStreamPrepareHardware = PrepareStream; callbacks.EvtAcxStreamReleaseHardware = ReleaseStream;
    callbacks.EvtAcxStreamRun = RunStream; callbacks.EvtAcxStreamPause = PauseStream;
    NTSTATUS status = AcxStreamInitAssignAcxStreamCallbacks(init, &callbacks);
    if (!NT_SUCCESS(status)) { InterlockedExchange(&b->Active, 0); return status; }
    ACX_RT_STREAM_CALLBACKS rt; ACX_RT_STREAM_CALLBACKS_INIT(&rt);
    rt.EvtAcxStreamAllocateRtPackets = AllocatePackets; rt.EvtAcxStreamFreeRtPackets = FreePackets;
    rt.EvtAcxStreamGetHwLatency = Latency; rt.EvtAcxStreamGetCurrentPacket = CurrentPacket; rt.EvtAcxStreamGetPresentationPosition = Position;
    if (c->Bus >= ST_MIC) rt.EvtAcxStreamGetCapturePacket = GetCapture; else rt.EvtAcxStreamSetRenderPacket = SetRender;
    status = AcxStreamInitAssignAcxRtStreamCallbacks(init, &rt);
    if (!NT_SUCCESS(status)) { InterlockedExchange(&b->Active, 0); return status; }
    AcxStreamInitSetAcxRtStreamSupportsNotifications(init);
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, STREAM); attributes.EvtDestroyCallback = DestroyStream;
    ACXSTREAM stream; status = AcxRtStreamCreate(device, circuit, &attributes, &init, &stream);
    if (!NT_SUCCESS(status)) { InterlockedExchange(&b->Active, 0); return status; }
    auto s = StreamContext(stream); s->Bus = b; s->Capture = c->Bus >= ST_MIC; s->Handle = stream;
    KeInitializeSpinLock(&s->Lock); ResetBus(b); return STATUS_SUCCESS;
}

static NTSTATUS AddCircuit(WDFDEVICE device, ULONG bus) {
    auto d = DeviceContext(device); auto init = AcxCircuitInitAllocate(device); if (!init) return STATUS_INSUFFICIENT_RESOURCES;
    GUID component = ComponentBase; component.Data1 += bus;
    AcxCircuitInitSetComponentId(init, &component);
    const wchar_t* names[] = { L"SoltexGame", L"SoltexChat", L"SoltexMedia", L"SoltexAux", L"SoltexMicrophone", L"SoltexStream" };
    UNICODE_STRING name; RtlInitUnicodeString(&name, names[bus]);
    NTSTATUS status = AcxCircuitInitAssignName(init, &name);
    if (NT_SUCCESS(status)) status = AcxCircuitInitAssignAcxCreateStreamCallback(init, CreateStream);
    AcxCircuitInitSetCircuitType(init, bus >= ST_MIC ? AcxCircuitTypeCapture : AcxCircuitTypeRender);
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, CIRCUIT);
    if (NT_SUCCESS(status)) status = AcxCircuitCreate(device, &attributes, &init, &d->Circuits[bus]);
    if (init) AcxCircuitInitFree(init); if (!NT_SUCCESS(status)) return status;
    auto circuit = d->Circuits[bus]; CircuitContext(circuit)->Device = d; CircuitContext(circuit)->Bus = bus;
    ACXPIN pins[2]; ACX_PIN_CONFIG pin; ACX_PIN_CONFIG_INIT(&pin);
    pin.Type = bus >= ST_MIC ? AcxPinTypeSource : AcxPinTypeSink;
    pin.Communication = bus >= ST_MIC ? AcxPinCommunicationSource : AcxPinCommunicationSink; pin.Category = &KSCATEGORY_AUDIO;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ParentObject = circuit;
    status = AcxPinCreate(circuit, &attributes, &pin, &pins[0]); if (!NT_SUCCESS(status)) return status;
    ACX_PIN_CONFIG_INIT(&pin); pin.Type = bus >= ST_MIC ? AcxPinTypeSink : AcxPinTypeSource;
    pin.Communication = AcxPinCommunicationNone; pin.Category = bus >= ST_MIC ? &KSNODETYPE_MICROPHONE : &KSNODETYPE_SPEAKER;
    status = AcxPinCreate(circuit, &attributes, &pin, &pins[1]); if (!NT_SUCCESS(status)) return status;
    ACX_JACK_CONFIG jackConfig; ACX_JACK_CONFIG_INIT(&jackConfig);
    jackConfig.Description.ChannelMapping = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
    jackConfig.Description.ConnectionType = AcxConnTypeAtapiInternal;
    jackConfig.Description.GeoLocation = AcxGeoLocNotApplicable;
    jackConfig.Description.GenLocation = AcxGenLocInternal;
    jackConfig.Description.PortConnection = AcxPortConnIntegratedDevice;
    ACXJACK jack; WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ParentObject = pins[1];
    status = AcxJackCreate(pins[1], &attributes, &jackConfig, &jack); if (!NT_SUCCESS(status)) return status;
    status = AcxPinAddJacks(pins[1], &jack, 1); if (!NT_SUCCESS(status)) return status;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ParentObject = circuit;
    KSDATAFORMAT_WAVEFORMATEXTENSIBLE pcm = {};
    pcm.DataFormat.FormatSize = sizeof(pcm); pcm.DataFormat.MajorFormat = KSDATAFORMAT_TYPE_AUDIO;
    pcm.DataFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM; pcm.DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX;
    pcm.WaveFormatExt.Format = { WAVE_FORMAT_EXTENSIBLE, ST_CHANNELS, ST_RATE, ST_RATE * ST_FRAME_BYTES, ST_FRAME_BYTES, 16, 22 };
    pcm.WaveFormatExt.Samples.wValidBitsPerSample = 16; pcm.WaveFormatExt.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
    pcm.WaveFormatExt.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    ACX_DATAFORMAT_CONFIG data; ACX_DATAFORMAT_CONFIG_INIT_KS(&data, &pcm); ACXDATAFORMAT format;
    status = AcxDataFormatCreate(device, &attributes, &data, &format); if (!NT_SUCCESS(status)) return status;
    status = AcxDataFormatListAddDataFormat(AcxPinGetRawDataFormatList(pins[0]), format); if (!NT_SUCCESS(status)) return status;
    return AcxCircuitAddPins(circuit, pins, 2);
}

NTSTATUS PrepareDevice(WDFDEVICE device, WDFCMRESLIST, WDFCMRESLIST) {
    auto d = DeviceContext(device);
    for (ULONG i = 0; i < ST_BUS_COUNT; ++i) {
        NTSTATUS status = AcxDeviceAddCircuit(device, d->Circuits[i]);
        if (!NT_SUCCESS(status)) { while (i) AcxDeviceRemoveCircuit(device, d->Circuits[--i]); return status; }
    }
    return STATUS_SUCCESS;
}
NTSTATUS ReleaseDevice(WDFDEVICE device, WDFCMRESLIST) {
    for (auto circuit : DeviceContext(device)->Circuits) AcxDeviceRemoveCircuit(device, circuit);
    return STATUS_SUCCESS;
}
void DeviceCleanup(WDFOBJECT object) {
    ExAcquireFastMutex(&GlobalLock);
    if (CurrentDevice == DeviceContext(object)) CurrentDevice = nullptr;
    ExReleaseFastMutex(&GlobalLock);
}
NTSTATUS DeviceAdd(WDFDRIVER, PWDFDEVICE_INIT init) {
    ACX_DEVICEINIT_CONFIG acx; ACX_DEVICEINIT_CONFIG_INIT(&acx);
    NTSTATUS status = AcxDeviceInitInitialize(init, &acx); if (!NT_SUCCESS(status)) return status;
    WDF_PNPPOWER_EVENT_CALLBACKS pnp; WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware = PrepareDevice; pnp.EvtDeviceReleaseHardware = ReleaseDevice; WdfDeviceInitSetPnpPowerEventCallbacks(init, &pnp);
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE); attributes.EvtCleanupCallback = DeviceCleanup;
    WDFDEVICE device; status = WdfDeviceCreate(&init, &attributes, &device); if (!NT_SUCCESS(status)) return status;
    ACX_DEVICE_CONFIG config; ACX_DEVICE_CONFIG_INIT(&config); status = AcxDeviceInitialize(device, &config); if (!NT_SUCCESS(status)) return status;
    auto d = DeviceContext(device);
    for (ULONG i = 0; i < ST_BUS_COUNT; ++i) { KeInitializeSpinLock(&d->Buses[i].Lock); status = AddCircuit(device, i); if (!NT_SUCCESS(status)) return status; }
    ExAcquireFastMutex(&GlobalLock);
    if (CurrentDevice) status = STATUS_DEVICE_BUSY; else CurrentDevice = d;
    ExReleaseFastMutex(&GlobalLock); return status;
}
void DriverUnload(WDFDRIVER) { if (ControlDevice) WdfObjectDelete(ControlDevice); }
extern "C" DRIVER_INITIALIZE DriverEntry;
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT object, PUNICODE_STRING path) {
    ExInitializeFastMutex(&GlobalLock);
    WDF_DRIVER_CONFIG config; WDF_DRIVER_CONFIG_INIT(&config, DeviceAdd); config.EvtDriverUnload = DriverUnload;
    WDFDRIVER driver; NTSTATUS status = WdfDriverCreate(object, path, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver); if (!NT_SUCCESS(status)) return status;
    ACX_DRIVER_CONFIG acx; ACX_DRIVER_CONFIG_INIT(&acx); status = AcxDriverInitialize(driver, &acx); if (!NT_SUCCESS(status)) return status;
    auto init = WdfControlDeviceInitAllocate(driver, &SDDL_DEVOBJ_SYS_ALL_ADM_ALL); if (!init) return STATUS_INSUFFICIENT_RESOURCES;
    UNICODE_STRING name; RtlInitUnicodeString(&name, L"\\Device\\SoltexAudioTransport"); status = WdfDeviceInitAssignName(init, &name);
    WdfDeviceInitSetExclusive(init, TRUE); WdfDeviceInitSetIoType(init, WdfDeviceIoBuffered);
    WDF_FILEOBJECT_CONFIG files; WDF_FILEOBJECT_CONFIG_INIT(&files, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, LeaseCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &files, WDF_NO_OBJECT_ATTRIBUTES);
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ExecutionLevel = WdfExecutionLevelPassive;
    if (NT_SUCCESS(status)) status = WdfDeviceCreate(&init, &attributes, &ControlDevice);
    if (init) WdfDeviceInitFree(init); if (!NT_SUCCESS(status)) return status;
    RtlInitUnicodeString(&name, L"\\DosDevices\\SoltexAudioTransport"); status = WdfDeviceCreateSymbolicLink(ControlDevice, &name); if (!NT_SUCCESS(status)) return status;
    WDF_IO_QUEUE_CONFIG queue; WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue, WdfIoQueueDispatchSequential); queue.EvtIoDeviceControl = Control;
    status = WdfIoQueueCreate(ControlDevice, &queue, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE); if (!NT_SUCCESS(status)) return status;
    WdfControlFinishInitializing(ControlDevice); return STATUS_SUCCESS;
}

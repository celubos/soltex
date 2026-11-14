// Soltex owned audio transport ABI v1. Original source; see README.md.
#pragma once
typedef unsigned int ST_U32;
typedef unsigned long long ST_U64;
enum : ST_U32 { ST_VERSION = 1, ST_BUS_COUNT = 6, ST_MIC = 4, ST_RATE = 48000,
    ST_CHANNELS = 2, ST_FRAME_BYTES = 4, ST_MAX_FRAMES = 4800, ST_RING_FRAMES = 9600 };
// METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA. All calls require the single privileged lease.
enum : ST_U32 { ST_IOCTL_QUERY = 0x22e000, ST_IOCTL_READ = 0x22e004, ST_IOCTL_WRITE = 0x22e008 };
struct ST_REQUEST { ST_U32 Version, Size, Bus, Frames; ST_U64 Sequence, Generation; };
struct ST_RESPONSE {
    ST_U32 Version, Size, Bus, Frames;
    ST_U64 Sequence, Generation, DroppedFrames, UnderrunFrames;
    ST_U32 Active, BufferedFrames;
};
static_assert(sizeof(ST_REQUEST) == 32 && sizeof(ST_RESPONSE) == 56, "Transport ABI packing changed");

// Caller supplies serialization (kernel spin lock). No allocation, floating point or OS calls.
struct ST_RING {
    unsigned char Bytes[ST_RING_FRAMES * ST_FRAME_BYTES];
    ST_U32 Head, Count;
    ST_U64 Produced, Consumed, Dropped, Underrun;
};
inline void StReset(ST_RING* r) { r->Head = r->Count = 0; r->Produced = r->Consumed = r->Dropped = r->Underrun = 0; }
inline void StPush(ST_RING* r, const unsigned char* data, ST_U32 frames) {
    for (ST_U32 f = 0; f < frames; ++f) {
        if (r->Count == ST_RING_FRAMES) { r->Head = (r->Head + 1) % ST_RING_FRAMES; --r->Count; ++r->Dropped; }
        ST_U32 at = ((r->Head + r->Count) % ST_RING_FRAMES) * ST_FRAME_BYTES;
        for (ST_U32 b = 0; b < ST_FRAME_BYTES; ++b) r->Bytes[at + b] = data[f * ST_FRAME_BYTES + b];
        ++r->Count; ++r->Produced;
    }
}
inline ST_U32 StPop(ST_RING* r, unsigned char* data, ST_U32 frames, bool pad) {
    const ST_U32 available = r->Count < frames ? r->Count : frames;
    for (ST_U32 f = 0; f < available; ++f) {
        const ST_U32 at = r->Head * ST_FRAME_BYTES;
        for (ST_U32 b = 0; b < ST_FRAME_BYTES; ++b) data[f * ST_FRAME_BYTES + b] = r->Bytes[at + b];
        r->Head = (r->Head + 1) % ST_RING_FRAMES; --r->Count; ++r->Consumed;
    }
    if (pad) {
        for (ST_U32 b = available * ST_FRAME_BYTES; b < frames * ST_FRAME_BYTES; ++b) data[b] = 0;
        r->Underrun += frames - available;
    }
    return available;
}
inline bool StValid(const ST_REQUEST& q) {
    return q.Version == ST_VERSION && q.Size == sizeof(ST_REQUEST) && q.Bus < ST_BUS_COUNT &&
        q.Frames <= ST_MAX_FRAMES;
}
inline bool StInject(ST_RING* ring, const unsigned char* data, ST_U32 frames,
    ST_U64 sequence, ST_U64 expectedGeneration, ST_U64 currentGeneration) {
    if (frames > ST_MAX_FRAMES || sequence != ring->Produced || expectedGeneration != currentGeneration) return false;
    StPush(ring, data, frames); return true;
}
enum ST_PACKET_RESULT { StPacketOkay, StPacketState, StPacketLate, StPacketOverrun, StPacketInvalid };
inline ST_PACKET_RESULT StCheckPacket(ST_U32 packet, ST_U32 current, ST_U32 count, bool running,
    bool ended, ST_U32 flags, ST_U32 eos, ST_U32 size) {
    if (ended || !count) return StPacketState;
    const int delta = static_cast<int>(packet - current);
    if (delta < 0 || (running && delta == 0)) return StPacketLate;
    if (packet - current >= count) return StPacketOverrun;
    if ((flags & ~0x200u) || ((flags & 0x200u) && (eos > size || eos % ST_FRAME_BYTES))) return StPacketInvalid;
    return StPacketOkay;
}

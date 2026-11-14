#include "../transport.h"
#include <cassert>
#include <cstdio>
#include <deque>
#include <random>
#include <vector>

int main() {
    ST_RING ring = {}; std::deque<ST_U32> reference; std::mt19937 random(0x5017e);
    ST_U64 produced = 0, consumed = 0, dropped = 0, underrun = 0;
    for (int operation = 0; operation < 12000; ++operation) {
        ST_U32 frames = random() % 600;
        std::vector<ST_U32> buffer(frames, 0xdeadbeef);
        if (random() % 2) {
            for (auto& value : buffer) { value = random(); reference.push_back(value); ++produced;
                if (reference.size() > ST_RING_FRAMES) { reference.pop_front(); ++dropped; } }
            StPush(&ring, reinterpret_cast<unsigned char*>(buffer.data()), frames);
        } else {
            bool pad = random() % 2 != 0;
            ST_U32 available = static_cast<ST_U32>(reference.size() < frames ? reference.size() : frames);
            assert(StPop(&ring, reinterpret_cast<unsigned char*>(buffer.data()), frames, pad) == available);
            for (ST_U32 i = 0; i < available; ++i) { assert(buffer[i] == reference.front()); reference.pop_front(); ++consumed; }
            for (ST_U32 i = available; i < frames; ++i) assert(buffer[i] == (pad ? 0u : 0xdeadbeefu));
            if (pad) underrun += frames - available;
        }
        assert(ring.Count == reference.size() && ring.Produced == produced && ring.Consumed == consumed);
        assert(ring.Dropped == dropped && ring.Underrun == underrun);
    }
    StReset(&ring); assert(ring.Count == 0 && ring.Produced == 0 && ring.Dropped == 0 && ring.Underrun == 0);
    ST_REQUEST q = {1,32,0,4800,0,0}; assert(StValid(q));
    q.Frames = 4801; assert(!StValid(q)); q.Frames = 0; q.Bus = 6; assert(!StValid(q));
    q.Bus = 0; q.Generation = 1; assert(StValid(q)); q.Generation = 0; q.Size = 31; assert(!StValid(q));
    q.Size = 32; q.Version = 2; assert(!StValid(q));
    ST_RING buses[6] = {}; ST_U32 input = 0x12345678, output = 0xffffffff;
    StPush(&buses[0], reinterpret_cast<unsigned char*>(&input), 1);
    assert(StPop(&buses[1], reinterpret_cast<unsigned char*>(&output), 1, true) == 0 && output == 0);
    assert(StPop(&buses[0], reinterpret_cast<unsigned char*>(&output), 1, false) == 1 && output == input);
    StReset(&ring);
    assert(!StInject(&ring, reinterpret_cast<unsigned char*>(&input), 1, 0, 7, 8));
    assert(ring.Count == 0 && ring.Produced == 0); // A generation reset must reject before publishing stale PCM.
    assert(StInject(&ring, reinterpret_cast<unsigned char*>(&input), 1, 0, 8, 8));
    assert(!StInject(&ring, reinterpret_cast<unsigned char*>(&input), 1, 0, 8, 8));
    assert(ring.Count == 1);
    assert(StCheckPacket(0, 0, 2, false, false, 0, 0xffffffff, 1920) == StPacketOkay); // preroll ignores non-EOS length
    assert(StCheckPacket(0, 0, 2, true, false, 0, 0, 1920) == StPacketLate);
    assert(StCheckPacket(1, 0, 2, true, false, 0x200, 0, 1920) == StPacketOkay);
    assert(StCheckPacket(1, 0, 2, true, true, 0, 0, 1920) == StPacketState);
    assert(StCheckPacket(2, 0, 2, true, false, 0, 0, 1920) == StPacketOverrun);
    assert(StCheckPacket(1, 0, 2, true, false, 0x200, 1921, 1920) == StPacketInvalid);
    assert(StCheckPacket(1, 0, 2, true, false, 1, 0, 1920) == StPacketInvalid);
    assert(StCheckPacket(0, 0xffffffff, 2, true, false, 0, 0, 1920) == StPacketOkay); // rollover
    puts("PASS native transport: 12000 randomized FIFO/overflow/underrun operations, ABI bounds, reset, bus isolation");
}

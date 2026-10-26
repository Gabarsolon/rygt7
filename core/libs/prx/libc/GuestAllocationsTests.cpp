// Guest allocation registry: splitting, protection and unmapping against a page model, atomicity
// when validation, allocation or the host callback fails, and (--bench) the cost of unmapping from
// a large registry, which used to copy the whole registry for every unmapped allocation.
#include "prx/libc/include/GuestAllocations.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <new>
#include <random>
#include <stdexcept>
#include <string>
#include <tuple>

// Exercise allocation failure during staging, before host-memory callbacks run.
static long failAfter = -1;
void* operator new(std::size_t n) {
    if (failAfter == 0) throw std::bad_alloc();
    if (failAfter > 0) --failAfter;
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }

using namespace GuestAllocations;
static void check(bool ok, const char* reason) { if (!ok) throw std::runtime_error(reason); }
static void* ptr(std::uint64_t n) { return reinterpret_cast<void*>(n); }
using Row = std::tuple<std::uint64_t, std::size_t, bool, bool, std::uint64_t, std::size_t, bool>;
static std::vector<Row> snapshot() {
    std::vector<Row> result;
    for (const auto& r : GuestAllocationsAcquire_nid_postfix())
        result.emplace_back(r->address, r->bytes, r->readable, r->writable,
                            r->allocationAddress, r->allocationBytes, r->releasable);
    return result;
}
static void add(std::uint64_t address, std::size_t bytes) {
    Mutation m; m.Add(ptr(address), bytes, true, true);
}
static void clear() {
    std::vector<std::uint64_t> addresses;
    for (const auto& r : snapshot()) addresses.push_back(std::get<4>(r));
    std::sort(addresses.begin(), addresses.end());
    addresses.erase(std::unique(addresses.begin(), addresses.end()), addresses.end());
    Mutation m;
    for (auto a : addresses) m.Remove(ptr(a));
}
template<typename F> static void expectFailure(F&& f) {
    bool threw = false;
    try { f(); } catch (const std::exception&) { threw = true; }
    check(threw, "expected failure");
}

static void directedTests() {
    add(0x10000, 0x4000); add(0x20000, 0x1000);
    const auto initial = snapshot();
    { Mutation m; m.Protect(ptr(0x11000), 0x2000, true, false, []{}); }
    auto s = snapshot();
    check(s.size() == 4 && !std::get<3>(s[1]) && std::get<1>(s[1]) == 0x2000,
          "partial protection split");
    bool callback = false;
    { Mutation m; m.Unmap(ptr(0x11000), 0x2000, [&](auto a, auto n, auto owner, bool last) {
        callback = true;
        check(a == ptr(0x11000) && n == 0x2000 && owner == ptr(0x10000) && !last,
              "partial unmap callback");
    }); }
    check(callback && snapshot().size() == 3, "partial unmap");
    const auto hole = snapshot();
    expectFailure([&]{ Mutation m; m.Protect(ptr(0x10000), 0x4000, true, true, []{}); });
    check(snapshot() == hole, "protection crossing hole changed registry");
    expectFailure([&]{ Mutation m; m.Unmap(ptr(0x10000), 0x4000, [](auto...){ }); });
    check(snapshot() == hole, "unmap crossing internal hole changed registry");
    { Mutation m; m.Unmap(ptr(0x10000), 0x1000, [](auto, auto, auto, bool last) {
        check(!last, "first fragment incorrectly marked last");
    }); }
    { Mutation m; m.Unmap(ptr(0x13000), 0x1000, [](auto, auto, auto, bool last) {
        check(last, "last fragment not marked last");
    }); }
    check(snapshot().size() == 1, "unrelated allocation removed");
    clear();

    // Multiple allocations, including a gap, must give one final-release callback each.
    add(0x10000, 0x1000); add(0x11000, 0x1000); add(0x14000, 0x1000);
    int calls = 0;
    { Mutation m; m.Unmap(ptr(0x10000), 0x5000, [&](auto, auto n, auto, bool last) {
        ++calls; check(n == 0x1000 && last, "spanning unmap callback");
    }); }
    check(calls == 3 && snapshot().empty(), "spanning unmap contents");

    // Failure before/inside the host callback must leave the affected registry unchanged.
    add(0x10000, 0x4000); add(0x20000, 0x1000);
    const auto before = snapshot();
    expectFailure([&]{ Mutation m; m.Protect(ptr(0x11000), 0x1000, true, false,
        []{ throw std::runtime_error("simulated host protection failure"); }); });
    check(snapshot() == before, "failed host protect changed registry");
    expectFailure([&]{ Mutation m; m.Unmap(ptr(0x11000), 0x1000,
        [](auto...){ throw std::runtime_error("simulated host unmap failure"); }); });
    check(snapshot() == before, "failed host unmap changed registry");
    for (long n = 0; n != 6; ++n) {
        bool hostCalled = false;
        { Mutation m;
          failAfter = n;
          try { m.Protect(ptr(0x11000), 0x1000, true, false, [&]{ hostCalled = true; }); }
          catch (const std::bad_alloc&) {}
          failAfter = -1;
        }
        check(!hostCalled && snapshot() == before, "allocation failure was not atomic");
    }
    expectFailure([&]{ Mutation m; m.Protect(ptr(0x10000), 0, true, false, []{}); });
    expectFailure([&]{ Mutation m; m.Protect(ptr(0x10000), 0x1000, false, true, []{}); });
    expectFailure([&]{ Mutation m; m.Unmap(ptr(UINT64_MAX - 7), 16, [](auto...){ }); });
    check(snapshot() == before, "invalid input changed registry");
    clear();

    // Previous successful pieces remain committed if a later host unmap fails.
    add(0x10000, 0x1000); add(0x11000, 0x1000);
    calls = 0;
    expectFailure([&]{ Mutation m; m.Unmap(ptr(0x10000), 0x2000, [&](auto...){
        if (++calls == 2) throw std::runtime_error("second piece failure");
    }); });
    check(calls == 2 && snapshot().size() == 1 && std::get<0>(snapshot()[0]) == 0x11000,
          "multi-piece failure semantics");
    clear();
}

static void randomTests() {
    constexpr unsigned count = 24, pages = 32, pageSize = 4096;
    std::array<std::array<int, pages>, count> model;
    for (unsigned a = 0; a != count; ++a) {
        model[a].fill(2); add(0x100000ull + a * 0x40000, pages * pageSize);
    }
    std::mt19937 rng(7519);
    unsigned operations = 0;
    for (unsigned step = 0; step != 5000; ++step) {
        const unsigned a = rng() % count, start = rng() % pages;
        if (!model[a][start]) continue;
        unsigned end = start + 1;
        while (end < pages && model[a][end] && (rng() & 1)) ++end;
        const bool remove = rng() % 4 == 0;
        const bool writable = (rng() & 1) != 0;
        const auto base = 0x100000ull + a * 0x40000;
        bool expectedLast = true;
        for (unsigned j = 0; j != pages; ++j)
            if ((j < start || j >= end) && model[a][j]) expectedLast = false;
        { Mutation m;
          if (remove) m.Unmap(ptr(base + start * pageSize), (end - start) * pageSize,
              [&](auto address, auto bytes, auto owner, bool last) {
                  check(address == ptr(base + start * pageSize) && bytes == (end-start)*pageSize &&
                        owner == ptr(base) && last == expectedLast, "random unmap callback");
              });
          else m.Protect(ptr(base + start * pageSize), (end - start) * pageSize, true, writable, []{});
        }
        for (unsigned j = start; j != end; ++j) model[a][j] = remove ? 0 : writable ? 2 : 1;
        std::array<std::array<int, pages>, count> actual{};
        for (const auto& row : snapshot()) {
            const auto address = std::get<0>(row), owner = std::get<4>(row);
            const auto index = (owner - 0x100000ull) / 0x40000;
            check(index < count && std::get<5>(row) == pages*pageSize, "allocation metadata");
            for (unsigned j = (address-owner)/pageSize; j < (address-owner+std::get<1>(row))/pageSize; ++j) {
                check(!actual[index][j], "overlapping result ranges");
                actual[index][j] = std::get<3>(row) ? 2 : 1;
            }
        }
        check(actual == model, "randomized page model mismatch");
        ++operations;
    }
    clear();
    std::printf("random_operations=%u PASS\n", operations);
}

static void bench(unsigned count) {
    constexpr unsigned removed = 256;
    for (unsigned i = 0; i != count; ++i) add(0x10000000ull + i*0x1000ull, 0x1000);
    const auto start = std::chrono::steady_clock::now();
    { Mutation m; m.Unmap(ptr(0x10000000ull + (count/2)*0x1000ull), removed*0x1000,
        [](auto, auto, auto, bool last){ check(last, "bench final flag"); }); }
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now()-start).count();
    std::printf("ranges=%u removed=%u elapsed_ms=%.3f\n", count, removed, ms);
    check(snapshot().size() == count-removed, "benchmark contents"); clear();
}

int main(int argc, char** argv) {
    try {
        if (argc > 1 && std::string(argv[1]) == "--bench") { bench(4000); bench(20000); return 0; }
        directedTests(); randomTests();
        std::printf("directed_and_random_tests=PASS\n");
        return 0;
    } catch (const std::exception& e) {
        failAfter = -1;
        std::fprintf(stderr, "FAIL: %s\n", e.what()); return 1;
    }
}

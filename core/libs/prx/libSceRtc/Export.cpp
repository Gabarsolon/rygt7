#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#endif
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include "SceTypes.hpp"
#include "OnlineConfig.hpp"
#include "prx/libc/include/General.hpp"

namespace {

constexpr int SCE_RTC_ERROR_INVALID_POINTER = static_cast<int>(0x80B50002);
constexpr int SCE_RTC_ERROR_INVALID_VALUE = static_cast<int>(0x80B50003);
constexpr int SCE_RTC_ERROR_BAD_PARSE = static_cast<int>(0x80B50007);
constexpr int SCE_RTC_ERROR_INVALID_YEAR = static_cast<int>(0x80B50008);
constexpr int SCE_RTC_ERROR_INVALID_MONTH = static_cast<int>(0x80B50009);
constexpr int SCE_RTC_ERROR_INVALID_DAY = static_cast<int>(0x80B5000A);
constexpr int SCE_RTC_ERROR_INVALID_HOUR = static_cast<int>(0x80B5000B);
constexpr int SCE_RTC_ERROR_INVALID_MINUTE = static_cast<int>(0x80B5000C);
constexpr int SCE_RTC_ERROR_INVALID_SECOND = static_cast<int>(0x80B5000D);
constexpr int SCE_RTC_ERROR_INVALID_MICROSECOND = static_cast<int>(0x80B5000E);

constexpr std::int64_t TICKS_PER_SECOND = 1000000;
constexpr std::int64_t TICKS_PER_MINUTE = 60 * TICKS_PER_SECOND;
constexpr std::int64_t TICKS_PER_HOUR = 60 * TICKS_PER_MINUTE;
constexpr std::int64_t TICKS_PER_DAY = 24 * TICKS_PER_HOUR;
constexpr std::int64_t UNIX_EPOCH_DAYS = 719162;
constexpr std::int64_t WIN32_EPOCH_DAYS = 584388;
constexpr std::uint64_t UNIX_EPOCH_TICK = UNIX_EPOCH_DAYS * TICKS_PER_DAY;
constexpr std::uint64_t WIN32_EPOCH_TICK = WIN32_EPOCH_DAYS * TICKS_PER_DAY;
constexpr std::uint64_t MAX_TICK = 3652059ull * TICKS_PER_DAY - 1;

bool isLeapYear(std::int64_t year) {
    return (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
}

int daysInMonth(std::int64_t year, int month) {
    static constexpr int days[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return month == 2 && isLeapYear(year) ? 29 : days[month - 1];
}

std::int64_t daysFromCivil(std::int64_t year, unsigned month, unsigned day) {
    year -= month <= 2;
    const std::int64_t era = (year >= 0 ? year : year - 399) / 400;
    const auto yearOfEra = static_cast<unsigned>(year - era * 400);
    const unsigned dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
    const unsigned dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
    return era * 146097 + static_cast<std::int64_t>(dayOfEra) - 719468 + UNIX_EPOCH_DAYS;
}

void civilFromDays(std::int64_t days, std::int64_t& year, unsigned& month, unsigned& day) {
    days -= UNIX_EPOCH_DAYS - 719468;
    const std::int64_t era = (days >= 0 ? days : days - 146096) / 146097;
    const auto dayOfEra = static_cast<unsigned>(days - era * 146097);
    const unsigned yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
    const unsigned dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
    const unsigned shiftedMonth = (5 * dayOfYear + 2) / 153;
    day = dayOfYear - (153 * shiftedMonth + 2) / 5 + 1;
    month = shiftedMonth < 10 ? shiftedMonth + 3 : shiftedMonth - 9;
    year = static_cast<std::int64_t>(yearOfEra) + era * 400 + (month <= 2);
}

int validate(const RtcDateTime* time) {
    if (!time) return SCE_RTC_ERROR_INVALID_POINTER;
    if (time->year < 1 || time->year > 9999) return SCE_RTC_ERROR_INVALID_YEAR;
    if (time->month < 1 || time->month > 12) return SCE_RTC_ERROR_INVALID_MONTH;
    if (time->day < 1 || time->day > daysInMonth(time->year, time->month)) return SCE_RTC_ERROR_INVALID_DAY;
    if (time->hour > 23) return SCE_RTC_ERROR_INVALID_HOUR;
    if (time->minute > 59) return SCE_RTC_ERROR_INVALID_MINUTE;
    if (time->second > 59) return SCE_RTC_ERROR_INVALID_SECOND;
    if (time->microsecond >= TICKS_PER_SECOND) return SCE_RTC_ERROR_INVALID_MICROSECOND;
    return 0;
}

std::uint64_t toTick(const RtcDateTime& time) {
    return static_cast<std::uint64_t>(daysFromCivil(time.year, time.month, time.day)) * TICKS_PER_DAY
        + time.hour * TICKS_PER_HOUR + time.minute * TICKS_PER_MINUTE + time.second * TICKS_PER_SECOND + time.microsecond;
}

RtcDateTime fromTick(std::uint64_t tick) {
    std::int64_t year = 0;
    unsigned month = 0;
    unsigned day = 0;
    civilFromDays(static_cast<std::int64_t>(tick / TICKS_PER_DAY), year, month, day);
    const std::uint64_t timeOfDay = tick % TICKS_PER_DAY;
    return RtcDateTime{
        static_cast<std::uint16_t>(year), static_cast<std::uint16_t>(month), static_cast<std::uint16_t>(day),
        static_cast<std::uint16_t>(timeOfDay / TICKS_PER_HOUR), static_cast<std::uint16_t>(timeOfDay % TICKS_PER_HOUR / TICKS_PER_MINUTE),
        static_cast<std::uint16_t>(timeOfDay % TICKS_PER_MINUTE / TICKS_PER_SECOND), static_cast<std::uint32_t>(timeOfDay % TICKS_PER_SECOND)};
}

std::uint64_t currentTick() {
    const auto now = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    return UNIX_EPOCH_TICK + static_cast<std::uint64_t>(now);
}

// The PS4 network clock is NTP time, the same on every console, and GT Sport and GT7 time online
// races by it. A PC's own clock can be seconds off, so with online play on the network clock
// follows the host's RyGTServer (GET /rygt/time) instead, measured like NTP: of a few samples, the
// one with the shortest round trip, taking the reply to have been made halfway through it.
// Two players' race clocks must agree to a few milliseconds and never jump, so:
//   - the clock runs on the performance counter from a UTC reading taken once (the PC's wall
//     clock moves in timer steps and can be set while the game runs);
//   - it is measured every 15 s, and a sample whose round trip is well above the best seen (a lag
//     spike skews it) is not used;
//   - a new measurement is eased in, the clock running at most 5% fast or slow until it agrees,
//     so it never jumps or goes back; only the first measurement is taken at once.
std::atomic<std::int64_t> targetOffset{0};
std::atomic<bool> networkClockReady{false};
std::atomic<bool> takeOffsetAtOnce{false};  // set by the first measurement

#ifdef _WIN32
// Microseconds of UTC since year 1, from the performance counter after one precise UTC reading.
std::uint64_t steadyTick() {
    static LARGE_INTEGER frequency, start;
    static std::uint64_t startUtc = [] {
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&start);
        FILETIME now;
        GetSystemTimePreciseAsFileTime(&now);
        const std::uint64_t fileTime = (static_cast<std::uint64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        return UNIX_EPOCH_TICK + (fileTime - 116444736000000000ULL) / 10;
    }();
    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    const std::uint64_t elapsed = static_cast<std::uint64_t>(counter.QuadPart - start.QuadPart);
    const std::uint64_t ticks = static_cast<std::uint64_t>(frequency.QuadPart);
    return startUtc + elapsed / ticks * 1000000 + elapsed % ticks * 1000000 / ticks;
}

bool sampleHostClock(const RyGTOnline::Config& config, std::int64_t& offset, std::int64_t& roundTrip) {
    addrinfo hints{};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* address = nullptr;
    if (getaddrinfo(config.serverHost.c_str(), std::to_string(config.serverPort).c_str(), &hints, &address) != 0) return false;
    bool sampled = false;
    const SOCKET native = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (native != INVALID_SOCKET) {
        const DWORD timeout = 3000;
        const BOOL noDelay = TRUE;
        setsockopt(native, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        setsockopt(native, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
        setsockopt(native, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));
        if (connect(native, address->ai_addr, static_cast<int>(address->ai_addrlen)) == 0) {
            const std::string request = "GET /rygt/time HTTP/1.1\r\nHost: " + config.serverHost + "\r\nConnection: close\r\n\r\n";
            const std::uint64_t sent = steadyTick();
            std::uint64_t answered = 0;
            std::string response;
            if (send(native, request.data(), static_cast<int>(request.size()), 0) == static_cast<int>(request.size())) {
                char buffer[512];
                int received;
                while ((received = recv(native, buffer, sizeof(buffer), 0)) > 0) {
                    if (answered == 0) answered = steadyTick();
                    response.append(buffer, static_cast<std::size_t>(received));
                }
            }
            const auto body = response.find("\r\n\r\n");
            const long long host = body == std::string::npos ? 0 : std::atoll(response.c_str() + body + 4);
            if (answered != 0 && response.compare(0, 13, "HTTP/1.1 200 ") == 0 && host > 0) {
                roundTrip = static_cast<std::int64_t>(answered - sent);
                offset = static_cast<std::int64_t>(UNIX_EPOCH_TICK + static_cast<std::uint64_t>(host))
                    - static_cast<std::int64_t>(sent + static_cast<std::uint64_t>(roundTrip / 2));
                sampled = true;
            }
        }
        closesocket(native);
    }
    freeaddrinfo(address);
    return sampled;
}

void followHostClock(RyGTOnline::Config config) {
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
        networkClockReady = true;
        return;
    }
    std::int64_t bestEverRoundTrip = std::numeric_limits<std::int64_t>::max();
    bool everSynced = false;
    for (;;) {
        std::int64_t bestOffset = 0;
        std::int64_t bestRoundTrip = std::numeric_limits<std::int64_t>::max();
        for (int sample = 0; sample < 8; ++sample) {
            std::int64_t offset = 0;
            std::int64_t roundTrip = 0;
            if (!sampleHostClock(config, offset, roundTrip)) break;
            if (roundTrip < bestRoundTrip) {
                bestOffset = offset;
                bestRoundTrip = roundTrip;
            }
        }
        const bool sampled = bestRoundTrip != std::numeric_limits<std::int64_t>::max();
        if (sampled) {
            // The best round trip seen ages slowly, so a route that got slower is followed.
            if (bestEverRoundTrip != std::numeric_limits<std::int64_t>::max()) bestEverRoundTrip += bestEverRoundTrip / 20;
            bestEverRoundTrip = (std::min)(bestEverRoundTrip, bestRoundTrip);
            const bool clean = bestRoundTrip <= (std::max)(bestEverRoundTrip * 2, bestEverRoundTrip + 5000);
            if (clean) {
                const std::int64_t previous = targetOffset.exchange(bestOffset);
                if (!everSynced) takeOffsetAtOnce = true;
                if (!everSynced || std::llabs(bestOffset - previous) > 2000)
                    std::fprintf(stderr, "[online] network clock: host is %+.1f ms from this PC (round trip %.1f ms)%s\n",
                        bestOffset / 1000.0, bestRoundTrip / 1000.0, everSynced ? ", easing in" : "");
                everSynced = true;
            }
        } else if (!everSynced) {
            std::fprintf(stderr, "[online] network clock: RyGTServer at %s:%u did not answer; using this PC's clock\n",
                config.serverHost.c_str(), static_cast<unsigned>(config.serverPort));
        }
        networkClockReady = true;
        std::this_thread::sleep_for(std::chrono::seconds(everSynced ? 15 : 5));
    }
}
#endif

std::uint64_t currentNetworkTick() {
#ifdef _WIN32
    static const bool online = [] {
        const RyGTOnline::Config& config = RyGTOnline::Get();
        if (!config.enabled) return false;
        steadyTick();
        std::thread(followHostClock, config).detach();
        // Give the first measurement a moment, so the game doesn't start on this PC's clock.
        for (int waited = 0; waited < 150 && !networkClockReady; ++waited)
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        return true;
    }();
    if (online) {
        static std::mutex mutex;
        static std::int64_t applied = 0;
        static std::uint64_t lastNow = 0;
        const std::uint64_t now = steadyTick();
        std::lock_guard lock(mutex);
        const std::int64_t target = targetOffset.load();
        if (takeOffsetAtOnce.exchange(false) || lastNow == 0) {
            applied = target;
        } else {
            // Ease toward the measured offset: at most 5% of the time passed since the last read.
            const std::int64_t step = static_cast<std::int64_t>((now - lastNow) / 20);
            applied += (std::max)(-step, (std::min)(step, target - applied));
        }
        lastNow = now;
        return now + static_cast<std::uint64_t>(applied);
    }
#endif
    return currentTick();
}

std::int64_t localOffsetSeconds(std::uint64_t utcTick) {
    const auto seconds = static_cast<std::time_t>((static_cast<std::int64_t>(utcTick) - static_cast<std::int64_t>(UNIX_EPOCH_TICK)) / TICKS_PER_SECOND);
    std::tm local{};
#ifdef _WIN32
    if (localtime_s(&local, &seconds) != 0) return 0;
#else
    if (!localtime_r(&seconds, &local)) return 0;
#endif
    const std::int64_t localSeconds = (daysFromCivil(local.tm_year + 1900, static_cast<unsigned>(local.tm_mon + 1), static_cast<unsigned>(local.tm_mday)) - UNIX_EPOCH_DAYS) * 86400
        + local.tm_hour * 3600 + local.tm_min * 60 + local.tm_sec;
    return localSeconds - static_cast<std::int64_t>(seconds);
}

int addTicks(RtcTick* dst, const RtcTick* src, std::int64_t count, std::int64_t unit) {
    if (!dst || !src) return SCE_RTC_ERROR_INVALID_POINTER;
    if (count != 0 && (count > std::numeric_limits<std::int64_t>::max() / unit || count < std::numeric_limits<std::int64_t>::min() / unit)) return SCE_RTC_ERROR_INVALID_VALUE;
    const std::int64_t delta = count * unit;
    if (delta < 0 ? src->tick < static_cast<std::uint64_t>(-(delta + 1)) + 1 : MAX_TICK - src->tick < static_cast<std::uint64_t>(delta)) return SCE_RTC_ERROR_INVALID_VALUE;
    dst->tick = src->tick + static_cast<std::uint64_t>(delta);
    return 0;
}

int addMonths(RtcTick* dst, const RtcTick* src, std::int64_t months) {
    if (!dst || !src) return SCE_RTC_ERROR_INVALID_POINTER;
    RtcDateTime time = fromTick(src->tick);
    const std::int64_t monthIndex = time.year * 12 + (time.month - 1) + months;
    const std::int64_t year = monthIndex / 12;
    if (monthIndex < 12 || year > 9999) return SCE_RTC_ERROR_INVALID_VALUE;
    time.year = static_cast<std::uint16_t>(year);
    time.month = static_cast<std::uint16_t>(monthIndex % 12 + 1);
    const int lastDay = daysInMonth(time.year, time.month);
    if (time.day > lastDay) time.day = static_cast<std::uint16_t>(lastDay);
    dst->tick = toTick(time);
    return 0;
}

bool parseDigits(const char*& cursor, int count, int& value) {
    value = 0;
    for (int index = 0; index < count; ++index, ++cursor) {
        if (*cursor < '0' || *cursor > '9') return false;
        value = value * 10 + (*cursor - '0');
    }
    return true;
}

}

extern "C" {

int APS5_VABI sceRtcParseDateTime(RtcTick* tick, const char* date_time) {
    if (!tick || !date_time) return SCE_RTC_ERROR_INVALID_POINTER;
    return SCE_RTC_ERROR_BAD_PARSE;
}

int APS5_VABI sceRtcCheckValid(const RtcDateTime* time) {
    return validate(time);
}

int APS5_VABI sceRtcIsLeapYear(int year) {
    if (year < 1) return SCE_RTC_ERROR_INVALID_YEAR;
    return isLeapYear(year) ? 1 : 0;
}

int APS5_VABI sceRtcGetDaysInMonth(int year, int month) {
    if (year < 1) return SCE_RTC_ERROR_INVALID_YEAR;
    if (month < 1 || month > 12) return SCE_RTC_ERROR_INVALID_MONTH;
    return daysInMonth(year, month);
}

int APS5_VABI sceRtcGetDayOfWeek(int year, int month, int day) {
    if (year < 1 || year > 9999) return SCE_RTC_ERROR_INVALID_YEAR;
    if (month < 1 || month > 12) return SCE_RTC_ERROR_INVALID_MONTH;
    if (day < 1 || day > daysInMonth(year, month)) return SCE_RTC_ERROR_INVALID_DAY;
    return static_cast<int>((daysFromCivil(year, static_cast<unsigned>(month), static_cast<unsigned>(day)) + 1) % 7);
}

int APS5_VABI sceRtcGetTickResolution(void) {
    return static_cast<int>(TICKS_PER_SECOND);
}

int APS5_VABI sceRtcGetTick(const RtcDateTime* time, RtcTick* tick) {
    if (!tick) return SCE_RTC_ERROR_INVALID_POINTER;
    if (const int result = validate(time); result != 0) return result;
    tick->tick = toTick(*time);
    return 0;
}

int APS5_VABI sceRtcSetTick(RtcDateTime* time, const RtcTick* tick) {
    if (!time || !tick) return SCE_RTC_ERROR_INVALID_POINTER;
    if (tick->tick > MAX_TICK) return SCE_RTC_ERROR_INVALID_VALUE;
    *time = fromTick(tick->tick);
    return 0;
}

int APS5_VABI sceRtcGetCurrentTick(RtcTick* tick) {
    if (!tick) return SCE_RTC_ERROR_INVALID_POINTER;
    tick->tick = currentTick();
    return 0;
}

int APS5_VABI sceRtcGetCurrentNetworkTick(RtcTick* tick) {
    if (!tick) return SCE_RTC_ERROR_INVALID_POINTER;
    tick->tick = currentNetworkTick();
    return 0;
}

int APS5_VABI sceRtcGetCurrentClock(RtcDateTime* time, int time_zone_minutes) {
    if (!time) return SCE_RTC_ERROR_INVALID_POINTER;
    RtcTick tick{currentTick()};
    if (const int result = addTicks(&tick, &tick, time_zone_minutes, TICKS_PER_MINUTE); result != 0) return result;
    *time = fromTick(tick.tick);
    return 0;
}

int APS5_VABI sceRtcGetCurrentClockLocalTime(RtcDateTime* time) {
    if (!time) return SCE_RTC_ERROR_INVALID_POINTER;
    const std::uint64_t utc = currentTick();
    *time = fromTick(utc + static_cast<std::uint64_t>(localOffsetSeconds(utc) * TICKS_PER_SECOND));
    return 0;
}

int APS5_VABI sceRtcConvertUtcToLocalTime(const RtcTick* utc, RtcTick* local_time) {
    if (!utc || !local_time) return SCE_RTC_ERROR_INVALID_POINTER;
    return addTicks(local_time, utc, localOffsetSeconds(utc->tick), TICKS_PER_SECOND);
}

int APS5_VABI sceRtcConvertLocalTimeToUtc(const RtcTick* local_time, RtcTick* utc) {
    if (!local_time || !utc) return SCE_RTC_ERROR_INVALID_POINTER;
    const std::int64_t offset = localOffsetSeconds(local_time->tick - static_cast<std::uint64_t>(localOffsetSeconds(local_time->tick) * TICKS_PER_SECOND));
    return addTicks(utc, local_time, -offset, TICKS_PER_SECOND);
}

int APS5_VABI sceRtcGetTime_t(const RtcDateTime* time, int64_t* seconds) {
    if (!seconds) return SCE_RTC_ERROR_INVALID_POINTER;
    if (const int result = validate(time); result != 0) return result;
    const std::uint64_t tick = toTick(*time);
    if (tick < UNIX_EPOCH_TICK) return SCE_RTC_ERROR_INVALID_VALUE;
    *seconds = static_cast<int64_t>((tick - UNIX_EPOCH_TICK) / TICKS_PER_SECOND);
    return 0;
}

int APS5_VABI sceRtcSetTime_t(RtcDateTime* time, int64_t seconds) {
    if (!time) return SCE_RTC_ERROR_INVALID_POINTER;
    if (seconds < 0 || static_cast<std::uint64_t>(seconds) > (MAX_TICK - UNIX_EPOCH_TICK) / TICKS_PER_SECOND) return SCE_RTC_ERROR_INVALID_VALUE;
    *time = fromTick(UNIX_EPOCH_TICK + static_cast<std::uint64_t>(seconds) * TICKS_PER_SECOND);
    return 0;
}

int APS5_VABI sceRtcGetWin32FileTime(const RtcDateTime* time, uint64_t* win32_time) {
    if (!win32_time) return SCE_RTC_ERROR_INVALID_POINTER;
    if (const int result = validate(time); result != 0) return result;
    const std::uint64_t tick = toTick(*time);
    if (tick < WIN32_EPOCH_TICK) return SCE_RTC_ERROR_INVALID_VALUE;
    *win32_time = (tick - WIN32_EPOCH_TICK) * 10;
    return 0;
}

int APS5_VABI sceRtcSetWin32FileTime(RtcDateTime* time, uint64_t win32_time) {
    if (!time) return SCE_RTC_ERROR_INVALID_POINTER;
    if (win32_time / 10 > MAX_TICK - WIN32_EPOCH_TICK) return SCE_RTC_ERROR_INVALID_VALUE;
    *time = fromTick(WIN32_EPOCH_TICK + win32_time / 10);
    return 0;
}

int APS5_VABI sceRtcFormatRFC3339(char* date_time, const RtcTick* utc, int time_zone_minutes) {
    if (!date_time || !utc) return SCE_RTC_ERROR_INVALID_POINTER;
    RtcTick local{};
    if (const int result = addTicks(&local, utc, time_zone_minutes, TICKS_PER_MINUTE); result != 0) return result;
    const RtcDateTime time = fromTick(local.tick);
    int written = std::snprintf(date_time, 23, "%04u-%02u-%02uT%02u:%02u:%02u.%02u", time.year, time.month, time.day, time.hour, time.minute, time.second, time.microsecond / 10000);
    if (time_zone_minutes == 0) {
        std::snprintf(date_time + written, 2, "Z");
    } else {
        const int offset = time_zone_minutes < 0 ? -time_zone_minutes : time_zone_minutes;
        std::snprintf(date_time + written, 7, "%c%02d:%02d", time_zone_minutes < 0 ? '-' : '+', offset / 60, offset % 60);
    }
    return 0;
}

int APS5_VABI sceRtcFormatRFC2822(char* date_time, const RtcTick* utc, int time_zone_minutes) {
    if (!date_time || !utc) return SCE_RTC_ERROR_INVALID_POINTER;
    (void)time_zone_minutes;
    std::snprintf(date_time, 40, "Thu, 01 Jan 1970 00:00:00 +0000");
    return 0;
}

int APS5_VABI sceRtcParseRFC3339(RtcTick* utc, const char* date_time) {
    if (!utc || !date_time) return SCE_RTC_ERROR_INVALID_POINTER;
    const char* cursor = date_time;
    int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
    if (!parseDigits(cursor, 4, year) || *cursor++ != '-' || !parseDigits(cursor, 2, month) || *cursor++ != '-' || !parseDigits(cursor, 2, day)) return SCE_RTC_ERROR_BAD_PARSE;
    if (*cursor != 'T' && *cursor != 't' && *cursor != ' ') return SCE_RTC_ERROR_BAD_PARSE;
    ++cursor;
    if (!parseDigits(cursor, 2, hour) || *cursor++ != ':' || !parseDigits(cursor, 2, minute) || *cursor++ != ':' || !parseDigits(cursor, 2, second)) return SCE_RTC_ERROR_BAD_PARSE;
    std::uint32_t microsecond = 0;
    if (*cursor == '.') {
        ++cursor;
        std::uint32_t scale = 100000;
        if (*cursor < '0' || *cursor > '9') return SCE_RTC_ERROR_BAD_PARSE;
        for (; *cursor >= '0' && *cursor <= '9'; ++cursor, scale /= 10) microsecond += static_cast<std::uint32_t>(*cursor - '0') * scale;
    }
    std::int64_t offsetMinutes = 0;
    if (*cursor == 'Z' || *cursor == 'z') {
        ++cursor;
    } else if (*cursor == '+' || *cursor == '-') {
        const int sign = *cursor++ == '-' ? -1 : 1;
        int offsetHours = 0, offsetMinute = 0;
        if (!parseDigits(cursor, 2, offsetHours) || *cursor++ != ':' || !parseDigits(cursor, 2, offsetMinute)) return SCE_RTC_ERROR_BAD_PARSE;
        offsetMinutes = sign * (offsetHours * 60 + offsetMinute);
    } else {
        return SCE_RTC_ERROR_BAD_PARSE;
    }
    if (*cursor != 0) return SCE_RTC_ERROR_BAD_PARSE;
    const RtcDateTime time{static_cast<std::uint16_t>(year), static_cast<std::uint16_t>(month), static_cast<std::uint16_t>(day),
        static_cast<std::uint16_t>(hour), static_cast<std::uint16_t>(minute), static_cast<std::uint16_t>(second), microsecond};
    if (const int result = validate(&time); result != 0) return result;
    const RtcTick local{toTick(time)};
    return addTicks(utc, &local, -offsetMinutes, TICKS_PER_MINUTE);
}

int APS5_VABI sceRtcTickAddTicks(RtcTick* dst, const RtcTick* src, int64_t ticks) {
    return addTicks(dst, src, ticks, 1);
}

int APS5_VABI sceRtcTickAddMicroseconds(RtcTick* dst, const RtcTick* src, int64_t usec) {
    return addTicks(dst, src, usec, 1);
}

int APS5_VABI sceRtcTickAddSeconds(RtcTick* dst, const RtcTick* src, int64_t seconds) {
    return addTicks(dst, src, seconds, TICKS_PER_SECOND);
}

int APS5_VABI sceRtcTickAddMinutes(RtcTick* dst, const RtcTick* src, int64_t minutes) {
    return addTicks(dst, src, minutes, TICKS_PER_MINUTE);
}

int APS5_VABI sceRtcTickAddHours(RtcTick* dst, const RtcTick* src, int32_t hours) {
    return addTicks(dst, src, hours, TICKS_PER_HOUR);
}

int APS5_VABI sceRtcTickAddDays(RtcTick* dst, const RtcTick* src, int32_t days) {
    return addTicks(dst, src, days, TICKS_PER_DAY);
}

int APS5_VABI sceRtcTickAddWeeks(RtcTick* dst, const RtcTick* src, int32_t weeks) {
    return addTicks(dst, src, weeks, 7 * TICKS_PER_DAY);
}

int APS5_VABI sceRtcTickAddMonths(RtcTick* dst, const RtcTick* src, int32_t months) {
    return addMonths(dst, src, months);
}

int APS5_VABI sceRtcTickAddYears(RtcTick* dst, const RtcTick* src, int16_t years) {
    return addMonths(dst, src, static_cast<std::int64_t>(years) * 12);
}

}

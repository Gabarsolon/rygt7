#pragma once
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>

// Online settings shared by the NP and HTTP libraries. PSN is replaced by RyGTServer: when online
// is enabled the user is reported as signed in under a local identity, and the game's HTTP(S)
// traffic is sent as plain HTTP to the configured RyGTServer, which can run on this machine or a
// friend's.
//
// RyGT.ini (next to GTSport.exe) keys, each overridable by an environment variable:
//   online=1                       RYGT_ONLINE
//   online_id=RyGTPlayer           RYGT_ONLINE_ID     (PSN online ID, 3-16 characters)
//   online_server=127.0.0.1:7890   RYGT_ONLINE_SERVER (RyGTServer host[:port])
//   online_address=26.12.34.56     RYGT_ONLINE_ADDRESS (this PC's address as other players reach
//                                  it; by default the address on the network RyGTServer is on)
namespace RyGTOnline {

struct Config {
    bool enabled = false;
    std::string onlineId = "RyGTPlayer";
    std::string serverHost = "127.0.0.1";
    std::uint16_t serverPort = 7890;
    std::string address;  // online_address, empty when not set
    std::uint64_t accountId = 0;
};

namespace detail {

inline std::string_view Trim(std::string_view text) {
    const auto first = text.find_first_not_of(" \t\r\n");
    return first == text.npos ? std::string_view{} : text.substr(first, text.find_last_not_of(" \t\r\n") - first + 1);
}

inline void Apply(Config& config, std::string_view key, std::string_view value) {
    value = Trim(value);
    if (key == "online") {
        config.enabled = value == "1" || value == "true" || value == "yes" || value == "on";
    } else if (key == "online_id") {
        if (value.size() >= 3) config.onlineId = std::string(value.substr(0, 16));
    } else if (key == "online_address") {
        config.address = std::string(value);
    } else if (key == "online_server" && !value.empty()) {
        const auto colon = value.rfind(':');
        if (colon != value.npos && value.find(':') == colon) {
            config.serverHost = std::string(value.substr(0, colon));
            const int port = std::atoi(std::string(value.substr(colon + 1)).c_str());
            if (port > 0 && port < 65536) config.serverPort = static_cast<std::uint16_t>(port);
        } else {
            config.serverHost = std::string(value);
        }
    }
}

// The account ID is derived from the online ID so the same name always maps to the same account,
// on every machine joining the same server.
inline std::uint64_t AccountIdFor(std::string_view onlineId) {
    std::uint64_t hash = 14695981039346656037ull;
    for (char c : onlineId) {
        hash ^= static_cast<unsigned char>(c);
        hash *= 1099511628211ull;
    }
    return hash & 0x7FFFFFFFFFFFFFFFull;
}

inline Config Load() {
    Config config;
    try {
        std::ifstream input(std::filesystem::current_path() / "RyGT.ini");
        std::string line;
        while (std::getline(input, line)) {
            const auto separator = line.find('=');
            if (separator == std::string::npos) continue;
            Apply(config, Trim(std::string_view(line).substr(0, separator)), std::string_view(line).substr(separator + 1));
        }
    } catch (...) {
    }
    if (const char* value = std::getenv("RYGT_ONLINE")) Apply(config, "online", value);
    if (const char* value = std::getenv("RYGT_ONLINE_ID")) Apply(config, "online_id", value);
    if (const char* value = std::getenv("RYGT_ONLINE_SERVER")) Apply(config, "online_server", value);
    if (const char* value = std::getenv("RYGT_ONLINE_ADDRESS")) Apply(config, "online_address", value);
    config.accountId = AccountIdFor(config.onlineId);
    return config;
}

}

inline const Config& Get() {
    static const Config config = detail::Load();
    return config;
}

inline bool Enabled() { return Get().enabled; }

}

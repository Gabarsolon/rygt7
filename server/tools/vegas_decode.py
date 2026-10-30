"""Decodes (and encodes) GT7's online service ("Vegas") message bodies.

GT7 (CUSA24769, client 5.6.0) sends its API requests to api.<stage>.vegas.granturismo-online.net
with Content-Type application/octet-stream. A body is:

  bytes 0-1    01 01          format version
  byte  2      0x00 / 0x80    0x80 when the payload is NOT zstd-compressed (small payloads)
  byte  3      00
  bytes 4-15   ChaCha20 nonce (12 random bytes; IETF ChaCha20, 20 rounds, counter 0)
  bytes 16-47  HMAC-SHA256 of the ciphertext (bytes 48-) with HMAC_KEY
  bytes 48-    ChaCha20(CIPHER_KEY, nonce) of the payload: a zstd frame of JSON
               {"body": {...}, "headers": {app_name, app_version, client_version, sequence_no,
                client_request_id, window_name, boot_session_id, ...}}

Both keys are base64 strings in GT7's executable (next to "VegasPoolTransport"). Responses are
assumed to use the same format.

Usage: vegas_decode.py <body file>...         prints each payload
       vegas_decode.py --encode <json file>   writes <json file>.bin (needs the zstd tool)"""
import base64, hashlib, hmac, os, subprocess, sys, tempfile

from Crypto.Cipher import ChaCha20

CIPHER_KEY = base64.b64decode("FojitMgHIaZ4ZV+QrNZvv7OaGvxQJxJvlzZPuTM02X8=")
HMAC_KEY = base64.b64decode("2CinLKRqDQC3ScL8QkCuvSD+34OJiPM1EkOpYM3z7d8Au8lzMEaiZYEQ929VjnJGkA2cQeZvfbKmGN8NN/VhHg==")
HEADER = 48


def zstd(data, decompress):
    with tempfile.TemporaryDirectory() as folder:
        source, target = os.path.join(folder, "in"), os.path.join(folder, "out")
        with open(source, "wb") as f:
            f.write(data)
        subprocess.run(["zstd", "-qf", "-d" if decompress else "-3", source, "-o", target], check=True)
        with open(target, "rb") as f:
            return f.read()


def decode(body):
    if len(body) < HEADER or body[:2] != b"\x01\x01":
        raise ValueError("not a Vegas body")
    nonce, tag, ciphertext = body[4:16], body[16:48], body[48:]
    if not hmac.compare_digest(hmac.new(HMAC_KEY, ciphertext, hashlib.sha256).digest(), tag):
        raise ValueError("HMAC mismatch")
    payload = ChaCha20.new(key=CIPHER_KEY, nonce=nonce).decrypt(ciphertext)
    return payload if body[2] & 0x80 else zstd(payload, True)


def encode(payload, compress=True):
    nonce = os.urandom(12)
    ciphertext = ChaCha20.new(key=CIPHER_KEY, nonce=nonce).encrypt(zstd(payload, False) if compress else payload)
    flags = b"\x00" if compress else b"\x80"
    return b"\x01\x01" + flags + b"\x00" + nonce + hmac.new(HMAC_KEY, ciphertext, hashlib.sha256).digest() + ciphertext


def main(args):
    if args[:1] == ["--encode"]:
        for name in args[1:]:
            with open(name, "rb") as f, open(name + ".bin", "wb") as out:
                out.write(encode(f.read()))
        return
    for name in args:
        with open(name, "rb") as f:
            print("==", name)
            print(decode(f.read()).decode("utf-8", "replace"))


if __name__ == "__main__":
    main(sys.argv[1:])

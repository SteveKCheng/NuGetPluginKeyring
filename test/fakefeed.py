"""Minimal authenticated NuGet v3 feed, for testing the credential provider plugin.

Returns 401 until it sees the expected Basic credentials, and logs every request's
Authorization header so a test can prove which credentials arrived.
"""
import base64, json, os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8099
BASE = "http://127.0.0.1:%d" % PORT
EXPECTED = base64.b64encode(b"steve:s3cr3t-pat-value").decode()
LOG = os.environ.get("FAKE_FEED_LOG", "/tmp/fakefeed.log")

INDEX = {
    "version": "3.0.0",
    "resources": [
        {"@id": BASE + "/v3/flat2/", "@type": "PackageBaseAddress/3.0.0"},
        {"@id": BASE + "/v3/registration/", "@type": "RegistrationsBaseUrl/3.6.0"},
        {"@id": BASE + "/v3/query", "@type": "SearchQueryService/3.5.0"},
    ],
}

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *a):
        pass

    def record(self, text):
        with open(LOG, "a") as f:
            f.write(text + "\n")

    def do_GET(self):
        auth = self.headers.get("Authorization", "")
        if auth.startswith("Basic "):
            token = auth[6:]
            try:
                decoded = base64.b64decode(token).decode()
            except Exception:
                decoded = "<undecodable>"
            shown = "Basic " + decoded
        else:
            shown = auth or "<none>"
        self.record("%s %s | Authorization: %s" % (self.command, self.path, shown))

        if auth != "Basic " + EXPECTED:
            self.record("  -> 401")
            body = b'{"error":"unauthorized"}'
            self.send_response(401)
            self.send_header("WWW-Authenticate", 'Basic realm="fakefeed"')
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if self.path.startswith("/v3/index.json"):
            body = json.dumps(INDEX).encode()
            self.record("  -> 200 service index")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        # Authenticated, but we host no packages.
        self.record("  -> 404 (authenticated)")
        body = b"not found"
        self.send_response(404)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

if __name__ == "__main__":
    open(LOG, "w").close()
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()

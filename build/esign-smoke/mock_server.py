"""A stand-in TDPdf Signing service for the Windows smoke test (.github/workflows/esign-smoke.yml).

TDPdf is pointed at it with the ordinary TDPDF_DOCUMENSO_BASEURL environment variable (plain http
is accepted for a loopback host), so the real, unmodified EXE runs its real code paths against it:
browser sign-in (loopback + PKCE), the token probe, Signature Requests, the in-app signing window,
Resend reminder. It implements the same contract as our Documenso fork's patch 0004 and the v2
routes TDPdf calls, and records what it saw at /_state for the script to assert on.

Not shipped, not referenced by the app. Standard library only.
"""
import base64
import hashlib
import json
import sys
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
# The "signed copy" served for downloads: any real PDF (the workflow passes a demo file).
SIGNED_PDF = sys.argv[2] if len(sys.argv) > 2 else None
TOKEN = "api_SMOKEtoken0123456789abcdef"
CODE = "smoke-one-time-code"
ENV_ID = "envelope_smoke"
TEAM = "smoke-team"
SIGN_TOKEN = "tok_smoke_recipient"

lock = threading.Lock()
state = {
    "mode": "hold",          # hold: the sign-in page waits; allow: it redirects back with a code
    "authorize": [],         # every /connect/tdpdf?... the browser opened
    "exchanges": [],         # every token exchange, with whether PKCE verified
    "probes": 0,             # authenticated GET /api/v2/envelope
    "redistribute": [],
    "completed": False,      # flips when the signing page reaches /complete
    "pages": [],             # web pages served (path only)
}


def envelope():
    with lock:
        done = state["completed"]
    return {
        "id": ENV_ID,
        "title": "Smoke test agreement",
        "status": "COMPLETED" if done else "PENDING",
        "createdAt": "2026-10-16T10:00:00.000Z",
        "completedAt": "2026-10-16T10:05:00.000Z" if done else None,
        "team": {"id": 1, "url": TEAM},
        "user": {"id": 1, "email": "smoke@example.com", "name": "Smoke Owner"},
        "envelopeItems": [{"id": "item_smoke", "title": "smoke.pdf", "order": 1}],
        "recipients": [
            {
                "id": 11, "email": "signer@example.com", "name": "Sam Signer", "role": "SIGNER",
                "signingStatus": "SIGNED" if done else "NOT_SIGNED", "readStatus": "OPENED",
                "sendStatus": "SENT", "token": SIGN_TOKEN, "signingOrder": None,
                "signedAt": "2026-10-16T10:05:00.000Z" if done else None,
            },
            {
                "id": 12, "email": "copy@example.com", "name": "Casey Copy", "role": "CC",
                "signingStatus": "NOT_SIGNED", "readStatus": "NOT_OPENED", "sendStatus": "SENT",
                "token": "tok_cc", "signingOrder": None, "signedAt": None,
            },
        ],
    }


PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>{title}</title>
<style>body{{font-family:Segoe UI,sans-serif;padding:2rem;background:#f4f4f4}}h1{{color:#2e7d32}}</style></head>
<body><h1>{heading}</h1><p>{body}</p>{extra}</body></html>"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("mock: " + (fmt % args) + "\n")

    def _send(self, code, body, ctype="application/json", headers=None):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def _json(self, code, obj):
        self._send(code, json.dumps(obj))

    def _html(self, title, heading, body, extra=""):
        self._send(200, PAGE.format(title=title, heading=heading, body=body, extra=extra), "text/html; charset=utf-8")

    def _authed(self):
        return self.headers.get("Authorization") == TOKEN

    def do_GET(self):
        u = urllib.parse.urlsplit(self.path)
        q = urllib.parse.parse_qs(u.query)
        p = u.path

        if p == "/_state":
            with lock:
                return self._json(200, state)
        if p == "/_mode":
            with lock:
                state["mode"] = q.get("m", ["hold"])[0]
            return self._json(200, {"mode": state["mode"]})
        if p == "/api/health":
            return self._json(200, {"status": "ok"})
        if p == "/api/certificate-status":
            return self._json(200, {"isAvailable": True})

        if p == "/connect/tdpdf":
            if not q:
                # TDPdf's support probe. The real page redirects to /signin; any non-404 will do.
                return self._html("Sign in", "TDPdf Signing (smoke)", "Sign in to continue.")
            with lock:
                state["authorize"].append({k: v[0] for k, v in q.items()})
                mode = state["mode"]
            if mode == "allow":
                redirect = q["redirect_uri"][0] + "?" + urllib.parse.urlencode({"state": q["state"][0], "code": CODE})
                return self._send(302, "", "text/plain", {"Location": redirect})
            return self._html("Connect TDPdf", "Connect TDPdf (smoke)",
                              "Waiting — the smoke test cancels this sign-in from TDPdf.")

        if p.startswith("/api/v2/"):
            if not self._authed():
                return self._json(401, {"code": "UNAUTHORIZED", "message": "x"})
            if p == "/api/v2/envelope":
                with lock:
                    state["probes"] += 1
                return self._json(200, {"data": [envelope()], "count": 1, "currentPage": 1, "perPage": 20, "totalPages": 1})
            if p == f"/api/v2/envelope/{ENV_ID}":
                return self._json(200, envelope())
            if p == "/api/v2/envelope/item/item_smoke/download" and SIGNED_PDF:
                with lock:
                    state["downloads"] = state.get("downloads", 0) + 1
                with open(SIGNED_PDF, "rb") as f:
                    return self._send(200, f.read(), "application/pdf")
            return self._json(404, {"code": "NOT_FOUND"})

        # ── Web pages the in-app window shows ────────────────────────────────────────────
        with lock:
            state["pages"].append(p)
        if p == f"/t/{TEAM}/documents/{ENV_ID}":
            # Tries to leave the service after a moment: TDPdf must hand that to the default
            # browser and stay here.
            return self._html("Smoke request page", "Smoke request page", "This is the request page.",
                              "<script>setTimeout(function(){location.href='https://example.com/smoke-offsite';},2500);</script>")
        if p == f"/t/{TEAM}/documents/{ENV_ID}/logs":
            return self._html("Audit log page", "Audit log page", "Who did what, and when.")
        if p == f"/sign/{SIGN_TOKEN}":
            return self._html("Smoke signing page", "Smoke signing page", "Signing…",
                              f"<script>setTimeout(function(){{location.href='/sign/{SIGN_TOKEN}/complete';}},3000);</script>")
        if p == f"/sign/{SIGN_TOKEN}/complete":
            with lock:
                state["completed"] = True
            return self._html("Document signed", "Document signed", "Everyone has signed.")
        if p == "/signin":
            return self._html("Sign in", "Sign in", "Smoke sign-in page.")
        return self._send(404, "not found", "text/plain")

    def do_POST(self):
        u = urllib.parse.urlsplit(self.path)
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n) if n else b""
        try:
            body = json.loads(raw or b"{}")
        except ValueError:
            body = {}

        if u.path == "/api/connect/token":
            verifier = body.get("code_verifier", "")
            with lock:
                challenge = state["authorize"][-1].get("code_challenge") if state["authorize"] else None
            calc = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
            ok = body.get("code") == CODE and challenge is not None and calc == challenge
            with lock:
                state["exchanges"].append({"keys": sorted(body.keys()), "pkceOk": ok,
                                           "authorization": self.headers.get("Authorization")})
            if not ok:
                return self._json(400, {"error": "invalid_grant"})
            return self._send(200, json.dumps({"token": TOKEN}), headers={"Cache-Control": "no-store"})

        if not self._authed():
            return self._json(401, {"code": "UNAUTHORIZED"})
        if u.path == "/api/v2/envelope/get-many":
            return self._json(200, {"data": [envelope()]})
        if u.path == "/api/v2/envelope/redistribute":
            with lock:
                state["redistribute"].append(body)
            return self._json(200, {"success": True, "id": ENV_ID, "recipients": []})
        return self._json(404, {"code": "NOT_FOUND"})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()

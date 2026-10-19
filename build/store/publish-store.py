#!/usr/bin/env python3
"""
Points TDPdf's Microsoft Store listing at a new installer URL and submits it for certification.

TDPdf is an "EXE or MSI" Store app: the Store holds a URL to our signed installer, not a
package. So a Store update is: repoint the existing package's packageUrl, commit, wait until
the draft is ready, submit. Listing text, screenshots and every other package field (silent
switches, languages, architecture, error codes) carry forward untouched, because only
packageUrl is PATCHed.

Uses the Microsoft Store submission API for MSI or EXE apps directly
(https://learn.microsoft.com/windows/apps/publish/store-submission-api), standard library only.
Not the msstore CLI: it is not on NuGet (`dotnet tool install MSStore.CLI` fails), and on a
workstation it waits on an interactive credential prompt.

    MSSTORE_TENANT_ID MSSTORE_CLIENT_ID MSSTORE_CLIENT_SECRET MSSTORE_SELLER_ID MSSTORE_PRODUCT_ID
    python3 build/store/publish-store.py <installer-url> [--dry-run]

--dry-run reads the current package and readiness and changes nothing.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.store.microsoft.com"


def env(name):
    v = os.environ.get(name, "").strip()
    if not v:
        sys.exit(f"error: {name} is not set")
    return v


def token():
    body = urllib.parse.urlencode({
        "client_id": env("MSSTORE_CLIENT_ID"),
        "client_secret": env("MSSTORE_CLIENT_SECRET"),
        "scope": "https://api.store.microsoft.com/.default",
        "grant_type": "client_credentials",
    }).encode()
    url = f"https://login.microsoftonline.com/{env('MSSTORE_TENANT_ID')}/oauth2/v2.0/token"
    with urllib.request.urlopen(urllib.request.Request(url, body), timeout=60) as r:
        return json.load(r)["access_token"]


class Store:
    def __init__(self):
        self.tok = token()
        self.seller = env("MSSTORE_SELLER_ID")
        self.product = env("MSSTORE_PRODUCT_ID")

    def call(self, method, path, body=None):
        url = path if path.startswith("http") else f"{API}{path}"
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data, method=method, headers={
            "Authorization": f"Bearer {self.tok}",
            "X-Seller-Account-Id": self.seller,
            "Content-Type": "application/json",
        })
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                raw = r.read()
        except urllib.error.HTTPError as e:
            raw = e.read()
            sys.exit(f"error: {method} {path} -> HTTP {e.code}: {raw[:2000].decode(errors='replace')}")
        d = json.loads(raw) if raw else {}
        if d.get("isSuccess") is False:
            sys.exit(f"error: {method} {path} failed: {json.dumps(d.get('errors'))}")
        for e in d.get("errors") or []:
            print(f"  note from the Store: {e.get('code')}: {e.get('message')} ({e.get('target')})")
        return d.get("responseData") or {}

    def p(self, suffix=""):
        return f"/submission/v1/product/{self.product}{suffix}"

    def wait_ready(self, minutes=20):
        deadline = time.time() + minutes * 60
        while True:
            s = self.call("GET", self.p("/status"))
            if s.get("ongoingSubmissionId"):
                sys.exit(f"error: a submission is already in progress ({s['ongoingSubmissionId']}); "
                         "wait for it to finish certification, then run this again")
            if s.get("isReady"):
                return
            if time.time() > deadline:
                sys.exit("error: the draft did not become ready in time")
            print("  waiting for the Store to finish processing the package...")
            time.sleep(20)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry = "--dry-run" in sys.argv
    if len(args) != 1 or not args[0].startswith("https://"):
        sys.exit(__doc__)
    url = args[0]

    s = Store()
    pkgs = s.call("GET", s.p("/packages")).get("packages") or []
    if not pkgs:
        sys.exit("error: the product has no packages; the first submission is made by hand in Partner Center")
    for pk in pkgs:
        print(f"package {pk.get('packageId')}: {pk.get('packageUrl')}")

    if dry:
        st = s.call("GET", s.p("/status"))
        print(f"dry run: would repoint {len(pkgs)} package(s) at {url}; draft ready = {st.get('isReady')}"
              + (f", ongoing submission {st['ongoingSubmissionId']}" if st.get("ongoingSubmissionId") else ""))
        return

    s.wait_ready()
    for pk in pkgs:
        if pk.get("packageUrl") == url:
            print(f"package {pk['packageId']} already points at {url}")
            continue
        s.call("PATCH", s.p(f"/packages/{pk['packageId']}"), {"packageUrl": url})
        print(f"package {pk['packageId']} now points at {url}")

    commit = s.call("POST", s.p("/packages/commit"))
    print(f"committed; polling {commit.get('pollingUrl') or 'status'}")
    s.wait_ready()

    sub = s.call("POST", s.p("/submit"))
    sid = sub.get("submissionId")
    if not sid:
        sys.exit(f"error: the Store did not return a submission id: {json.dumps(sub)}")
    print(f"submitted: submission {sid}")
    status = s.call("GET", s.p(f"/submission/{sid}/status"))
    print(f"submission status: {json.dumps(status)}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as f:
            f.write(f"### Submitted to the Microsoft Store\n\nSubmission `{sid}`, installer {url}.\n"
                    "Certification usually takes up to three business days.\n")


if __name__ == "__main__":
    main()

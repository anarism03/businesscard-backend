"""
End-to-end smoke test for the BusinessCard API.

Usage:
    python scripts/smoke_test.py http://localhost:5000 <superadmin-email> <superadmin-password>

Creates a test company (random VÖEN) and employees, then checks auth, roles, brandColor, nickname,
scan timestamps, crop/recrop, audit log, export/import. Uses only the Python standard library.
"""
import io
import json
import random
import struct
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
import zipfile
import zlib
from datetime import datetime, timezone

BASE, SA_EMAIL, SA_PASSWORD = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
RUN = uuid.uuid4().hex[:6]  # keeps nicknames unique across repeated runs on the same database
passed, failed = 0, 0


def call(method, path, body=None, token=None, raw=False, headers=None, data=None, content_type=None):
    url = BASE + path
    h = dict(headers or {})
    if token:
        h["Authorization"] = "Bearer " + token
    if body is not None:
        data = json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    elif content_type:
        h["Content-Type"] = content_type
    req = urllib.request.Request(url, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            payload = r.read()
            return r.status, (payload if raw else (json.loads(payload) if payload else None)), dict(r.headers)
    except urllib.error.HTTPError as e:
        payload = e.read()
        try:
            parsed = json.loads(payload) if payload else None
        except ValueError:
            parsed = payload.decode(errors="replace")
        return e.code, parsed, dict(e.headers)


def check(name, condition, detail=""):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS  {name}")
    else:
        failed += 1
        print(f"  FAIL  {name}  {detail}")


def multipart(fields, file_bytes, filename="image.png", mime="image/png"):
    boundary = uuid.uuid4().hex
    out = io.BytesIO()
    for k, v in fields.items():
        out.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode())
    out.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
              f"Content-Type: {mime}\r\n\r\n".encode())
    out.write(file_bytes)
    out.write(f"\r\n--{boundary}--\r\n".encode())
    return out.getvalue(), f"multipart/form-data; boundary={boundary}"


def png(width, height):
    rows = []
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            row += bytes([(x * 7) % 256, (y * 5) % 256, 120])
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    signature = bytes([137, 80, 78, 71, 13, 10, 26, 10])
    return (signature + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def png_size(data):
    return struct.unpack(">II", data[16:24])


def login(email, password, voen=None):
    s, b, _ = call("POST", "/api/Auth/login", {"email": email, "password": password, "companyVoen": voen})
    return s, b


def complete_first_login(token, old_password, new_password):
    """New accounts must change their password before any business request (server-enforced)."""
    s, b, _ = call("POST", "/api/Auth/change-password", {"oldPassword": old_password, "newPassword": new_password}, token)
    assert s == 200, f"change-password failed: {s} {b}"


print("== Auth")
s, b = login(SA_EMAIL, SA_PASSWORD)
check("super admin login", s == 200 and b.get("accessToken"), f"{s} {b}")
SA = b["accessToken"]
s, b = login(SA_EMAIL, "wrong-password")
check("wrong password -> 401 INVALID_CREDENTIALS", s == 401 and b.get("code") == "INVALID_CREDENTIALS", f"{s} {b}")
s, _, _ = call("GET", "/api/SuperAdmin/dashboard")
check("no token -> 401", s == 401)

print("== Companies / brandColor")
voen = "".join(random.choice("0123456789") for _ in range(10))
s, created, _ = call("POST", "/api/SuperAdmin/companies",
                     {"name": "Smoke Test MMC", "voen": voen, "email": f"admin{voen}@test.local", "userLimit": 3}, SA)
check("create company returns admin credentials", s == 200 and created.get("adminPassword"), f"{s} {created}")
company_id = created["company"]["id"]
check("default brandColor #1b4a75", created["company"]["brandColor"] == "#1b4a75")
s, b, _ = call("POST", "/api/SuperAdmin/companies", {"name": "Dup", "voen": voen, "email": "x@test.local", "userLimit": 1}, SA)
check("duplicate VOEN -> 409", s == 409, f"{s} {b}")

s, b = login(created["adminEmail"], created["adminPassword"], voen)
check("company admin login (isFirstLogin=true)", s == 200 and b.get("isFirstLogin") is True, f"{s} {b}")
ADMIN = b["accessToken"]
s, b, _ = call("POST", "/api/Auth/change-password", {"oldPassword": "bad", "newPassword": "NewAdmin123!"}, ADMIN)
check("change-password wrong old -> 400 'Old password is incorrect'", s == 400 and "old password" in b["message"].lower(), f"{s} {b}")
s, b, _ = call("POST", "/api/Auth/change-password", {"oldPassword": created["adminPassword"], "newPassword": "NewAdmin123!"}, ADMIN)
check("change-password ok", s == 200, f"{s} {b}")
s, b, _ = call("GET", "/api/Auth/account-info", token=ADMIN)
check("account-info: isFirstLogin false + daysUntilPasswordExpiry", s == 200 and b["isFirstLogin"] is False and b["daysUntilPasswordExpiry"] >= 89, f"{s} {b}")

s, b, _ = call("PUT", "/api/CompanyAdmin/company", {"brandColor": "#15803D"}, ADMIN)
check("admin sets brandColor (normalized lower-case)", s == 200 and b["brandColor"] == "#15803d", f"{s} {b}")
s, b, _ = call("PUT", "/api/CompanyAdmin/company", {"name": "Smoke Test MMC 2"}, ADMIN)
check("omitted brandColor keeps value", s == 200 and b["brandColor"] == "#15803d", f"{s} {b}")
s, b, _ = call("PUT", "/api/CompanyAdmin/company", {"brandColor": "green"}, ADMIN)
check("invalid brandColor -> 400", s == 400 and b["code"] == "INVALID_BRAND_COLOR", f"{s} {b}")
s, b, _ = call("PUT", "/api/CompanyAdmin/company", {"userLimit": 999}, ADMIN)
check("company admin cannot change userLimit", s == 200 and b["userLimit"] == 3, f"{s} {b}")

print("== Users")
def new_user(email, **extra):
    body = {"companyId": company_id, "firstName": "Anar", "lastName": "Test", "jobTitle": "Dev", "phone1": "+994501112233",
            "email": email, "password": "Employee123!", "role": 2, "isActive": True,
            "contactInfos": [{"contactType": "phone", "value": "+994551234567", "label": "Mobil", "displayOrder": 0},
                             {"contactType": "meta", "value": "x", "label": "__setclapp_disabled_links__:phone2", "displayOrder": 1}],
            "socialAccounts": [{"platformName": "website::Sayt", "profileUrl": "https://example.az"}]}
    body.update(extra)
    return call("POST", "/api/CompanyAdmin/users", body, ADMIN)

s, e1, _ = new_user("e1@test.local")
check("create employee (canEdit=false, nickname=null)", s == 200 and e1["canEdit"] is False and e1["nickname"] is None, f"{s} {e1}")
s, e2, _ = new_user("e2@test.local")
s2, b, _ = new_user("E1@test.local")
check("duplicate e-mail in company -> 409 EMAIL_TAKEN", s2 == 409 and b["code"] == "EMAIL_TAKEN", f"{s2} {b}")
s, e3, _ = new_user("e3@test.local")
s, b, _ = new_user("e4@test.local")
check("user limit -> 400 USER_LIMIT_EXCEEDED", s == 400 and b["code"] == "USER_LIMIT_EXCEEDED", f"{s} {b}")
s, page, _ = call("GET", f"/api/CompanyAdmin/users/company/{company_id}?page=1&pageSize=1000", token=ADMIN)
check("list users {items,totalCount}", s == 200 and page["totalCount"] == 4 and len(page["items"]) == 4, f"{s} {page}")
check("contact metadata + order preserved", e1["contactInfos"][1]["label"] == "__setclapp_disabled_links__:phone2")

s, b = login("e1@test.local", "Employee123!", voen)
EMP1 = b["accessToken"]
complete_first_login(EMP1, "Employee123!", "Employee456!")
s, b = login("e2@test.local", "Employee123!", voen)
EMP2 = b["accessToken"]
complete_first_login(EMP2, "Employee123!", "Employee456!")
s, b, _ = call("PUT", "/api/User/profile", {"jobTitle": "Hack"}, EMP1)
check("employee with canEdit=false cannot edit profile -> 403", s == 403, f"{s} {b}")
s, b, _ = call("GET", f"/api/User/{e2['id']}", token=EMP1)
check("employee cannot read another employee -> 403", s == 403, f"{s}")
s, b, _ = call("GET", f"/api/CompanyAdmin/users/company/{company_id}", token=EMP1)
check("employee cannot use admin endpoints -> 403", s == 403, f"{s}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/canedit?canEdit=true", token=ADMIN)
check("admin enables canEdit", s == 200 and b["canEdit"] is True)
s, b, _ = call("PUT", "/api/User/profile", {"jobTitle": "Senior Dev", "photoUrl": ""}, EMP1)
check("employee edits own profile after canEdit", s == 200 and b["jobTitle"] == "Senior Dev", f"{s} {b}")

print("== Nickname")
s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": f"  Anar-Test{RUN}  "}, EMP2)
check("own nickname with canEdit=false, normalized", s == 200 and b == {"id": e2["id"], "nickname": f"anar-test{RUN}"}, f"{s} {b}")
s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": f"anar-test{RUN}"}, EMP2)
check("same nickname again is idempotent", s == 200 and b["nickname"] == f"anar-test{RUN}", f"{s} {b}")
s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": f"ANAR-TEST{RUN}"}, EMP1)
check("taken nickname (case-insensitive) -> 409 NICKNAME_TAKEN", s == 409 and b == {"code": "NICKNAME_TAKEN", "message": "Bu istifadəçi adı artıq istifadə olunur."}, f"{s} {b}")
for bad in ["ab", "-abc", "abc-", "ana r", "ənar", "a" * 31]:
    s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": bad}, EMP1)
    check(f"invalid nickname {bad!r} -> 400 INVALID_NICKNAME", s == 400 and b["code"] == "INVALID_NICKNAME", f"{s} {b}")
s, b, _ = call("PUT", "/api/User/profile/nickname", {}, EMP1)
check("missing nickname property -> 400", s == 400, f"{s} {b}")
s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": "x"}, None)
check("anonymous nickname change -> 401", s == 401, f"{s}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/nickname", {"nickname": f"e1_admin-set{RUN}"}, ADMIN)
check("admin sets employee nickname", s == 200 and b["nickname"] == f"e1_admin-set{RUN}", f"{s} {b}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{uuid.uuid4()}/nickname", {"nickname": "nobody"}, ADMIN)
check("admin nickname for unknown user -> 404", s == 404, f"{s}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/nickname", {"nickname": "zzz"}, EMP2)
check("employee cannot use admin nickname endpoint -> 403", s == 403, f"{s}")

# concurrency: two users race for the same name
results = []
def race(tok):
    results.append(call("PUT", "/api/User/profile/nickname", {"nickname": f"race-name{RUN}"}, tok)[0])
threads = [threading.Thread(target=race, args=(t,)) for t in (EMP1, EMP2)]
[t.start() for t in threads]; [t.join() for t in threads]
check("concurrent same nickname: exactly one 200", sorted(results) == [200, 409], f"{results}")
call("PUT", "/api/User/profile/nickname", {"nickname": f"anar-test{RUN}"}, EMP2)
call("PUT", "/api/User/profile/nickname", {"nickname": f"e1_admin-set{RUN}"}, EMP1)

# second company cannot reuse the nickname
voen2 = "".join(random.choice("0123456789") for _ in range(10))
s, c2, _ = call("POST", "/api/SuperAdmin/companies", {"name": "Other MMC", "voen": voen2, "email": f"o{voen2}@test.local", "userLimit": 5}, SA)
s, b = login(c2["adminEmail"], c2["adminPassword"], voen2)
ADMIN2 = b["accessToken"]
complete_first_login(ADMIN2, c2["adminPassword"], "NewAdmin123!")
s, b, _ = call("GET", "/api/Auth/account-info", token=ADMIN2)
s, b, _ = call("PUT", "/api/User/profile/nickname", {"nickname": f"Anar-Test{RUN}"}, ADMIN2)
check("nickname unique across companies -> 409", s == 409, f"{s} {b}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/nickname", {"nickname": "steal"}, ADMIN2)
check("cross-tenant admin nickname change -> 403", s == 403, f"{s} {b}")
s, b, _ = call("GET", f"/api/User/{e1['id']}", token=ADMIN2)
check("cross-tenant user read -> 403", s == 403, f"{s}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/active?isActive=false", token=ADMIN2)
check("cross-tenant deactivate -> 403", s == 403, f"{s}")

print("== Public card + scans")
before = datetime.now(timezone.utc)
s, by_uuid, headers = call("GET", f"/api/cards/{e2['id']}?source=nfc")
after = datetime.now(timezone.utc)
check("public card by UUID (anonymous)", s == 200 and by_uuid["id"] == e2["id"], f"{s} {by_uuid}")
check("public card has company brandColor", by_uuid.get("brandColor") == "#15803d", f"{by_uuid.get('brandColor')}")
check("public card not cacheable", "no-store" in headers.get("Cache-Control", ""), headers.get("Cache-Control"))
check("public card has no secrets", "passwordHash" not in json.dumps(by_uuid) and "role" not in by_uuid)
s, by_nick, _ = call("GET", f"/api/cards/ANAR-TEST{RUN}", headers={"User-Agent": "other-client"})
check("public card by nickname returns same real UUID", s == 200 and by_nick["id"] == e2["id"] and by_nick["nickname"] == f"anar-test{RUN}", f"{s}")
s, _, _ = call("GET", "/api/cards/no-such-name")
check("unknown nickname -> 404", s == 404)

s, logs, _ = call("GET", f"/api/Analytics/scans/logs?companyId={company_id}&page=1&pageSize=20", token=ADMIN)
nfc = [l for l in logs["items"] if l["source"] == "nfc" and l["employeeId"] == e2["id"]]
check("scan logged with source=nfc for the real UUID", len(nfc) == 1, f"{logs}")
if nfc:
    scanned = datetime.fromisoformat(nfc[0]["scannedAt"].replace("Z", "+00:00"))
    check("scannedAt is UTC with Z", nfc[0]["scannedAt"].endswith("Z"), nfc[0]["scannedAt"])
    check("scannedAt within request window (no minute drift)",
          before.timestamp() - 2 <= scanned.timestamp() <= after.timestamp() + 2,
          f"before={before.isoformat()} scanned={scanned.isoformat()} after={after.isoformat()}")
s, _, _ = call("GET", f"/api/cards/{e2['id']}?source=nfc")
s, logs2, _ = call("GET", f"/api/Analytics/scans/logs?companyId={company_id}", token=ADMIN)
check("immediate repeat from same client is de-duplicated", logs2["totalCount"] == logs["totalCount"], f"{logs2['totalCount']} vs {logs['totalCount']}")

s, cnt, _ = call("GET", f"/api/Analytics/scans/count?companyId={company_id}", token=ADMIN)
check("scan count", s == 200 and cnt["count"] == 2, f"{s} {cnt}")
s, chart, _ = call("GET", f"/api/Analytics/scans/chart?companyId={company_id}", token=ADMIN)
check("chart zero-filled daily points", s == 200 and len(chart) >= 30 and sum(p["count"] for p in chart) == 2, f"{s} {chart[-3:] if isinstance(chart, list) else chart}")
s, rank, _ = call("GET", f"/api/Analytics/employees/ranking?companyId={company_id}", token=ADMIN)
check("ranking first is e2 with 2 scans", s == 200 and rank[0]["employeeId"] == e2["id"] and rank[0]["scanCount"] == 2, f"{s} {rank[:2]}")
s, _, _ = call("GET", f"/api/Analytics/scans/count?companyId={c2['company']['id']}", token=ADMIN)
check("admin cannot read other company analytics -> 403", s == 403, f"{s}")

call("PUT", f"/api/CompanyAdmin/users/{e2['id']}/nickname", {"nickname": None}, ADMIN)
s, b, _ = call("GET", f"/api/cards/{e2['id']}")
check("after nickname removal UUID still works, nickname null", s == 200 and b["nickname"] is None, f"{s}")
s, _, _ = call("GET", f"/api/cards/anar-test{RUN}")
check("removed nickname no longer resolves", s == 404, f"{s}")

print("== Images: crop / recrop")
image = png(400, 300)
body, ctype = multipart({"x": 50, "y": 40, "width": 200, "height": 200, "sourceWidth": 400, "sourceHeight": 300}, image)
s, img, _ = call("POST", "/api/User/profile/photo", token=EMP1, data=body, content_type=ctype)
check("upload + crop own photo", s == 200 and img["width"] == 200 and img["height"] == 200 and img["crop"]["x"] == 50, f"{s} {img}")
s, pic, _ = call("GET", img["url"], raw=True)
check("cropped result is public JPEG (metadata stripped)", s == 200 and pic[:2] == bytes([0xFF, 0xD8]), f"{s}")
s, orig, _ = call("GET", img["originalUrl"], raw=True)
check("original requires auth", s == 401, f"{s}")
s, orig, _ = call("GET", img["originalUrl"], token=EMP1, raw=True)
check("original downloadable by owner, untouched", s == 200 and orig == image, f"{s}")
s, rec, _ = call("PUT", f"/api/images/{img['imageId']}/crop", {"x": 0, "y": 0, "width": 300, "height": 150, "sourceWidth": 400, "sourceHeight": 300}, EMP1)
check("recrop from original", s == 200 and rec["width"] == 300 and rec["height"] == 150 and rec["url"] != img["url"], f"{s} {rec}")
s, me, _ = call("GET", f"/api/User/{e1['id']}", token=EMP1)
check("photoUrl updated to recropped result", me["photoUrl"] == rec["url"], f"{me['photoUrl']}")
s, b, _ = call("PUT", f"/api/images/{img['imageId']}/crop", {"x": 300, "y": 0, "width": 300, "height": 150, "sourceWidth": 400, "sourceHeight": 300}, EMP1)
check("crop outside bounds -> 400", s == 400, f"{s} {b}")
body, ctype = multipart({"x": 0, "y": 0, "width": 10, "height": 10, "sourceWidth": 999, "sourceHeight": 300}, image)
s, b, _ = call("POST", "/api/User/profile/photo", token=EMP1, data=body, content_type=ctype)
check("source size mismatch -> 400", s == 400 and b["code"] == "SOURCE_SIZE_MISMATCH", f"{s} {b}")
body, ctype = multipart({}, b"not an image", "evil.png")
s, b, _ = call("POST", "/api/User/profile/photo", token=EMP1, data=body, content_type=ctype)
check("non-image upload -> 400", s == 400, f"{s} {b}")
body, ctype = multipart({}, image)
s, b, _ = call("POST", "/api/User/profile/card-background", token=EMP2, data=body, content_type=ctype)
check("canEdit=false employee cannot upload background -> 403", s == 403, f"{s}")
body, ctype = multipart({"x": 0, "y": 0, "width": 400, "height": 150, "sourceWidth": 400, "sourceHeight": 300}, image)
s, bg, _ = call("POST", f"/api/CompanyAdmin/users/{e2['id']}/card-background", token=ADMIN, data=body, content_type=ctype)
check("admin uploads/crops another employee's background", s == 200 and bg["kind"] == "cardBackground", f"{s} {bg}")
s, b, _ = call("PUT", f"/api/images/{bg['imageId']}/crop", {"x": 0, "y": 0, "width": 10, "height": 10, "sourceWidth": 400, "sourceHeight": 300}, ADMIN2)
check("other company cannot recrop -> 403", s == 403, f"{s}")
body, ctype = multipart({}, image)
s, logo, _ = call("POST", "/api/CompanyAdmin/company/logo", token=ADMIN, data=body, content_type=ctype)
check("company logo upload (no crop = whole image)", s == 200 and logo["width"] == 400 and logo["height"] == 300, f"{s} {logo}")

print("== Export / import / audit")
s, vcf, _ = call("GET", f"/api/ExportImport/vcf/{e1['id']}", token=ADMIN, raw=True)
check("vcf export", s == 200 and vcf.startswith(b"BEGIN:VCARD") and b"PHOTO" in vcf, f"{s}")
s, xlsx, _ = call("GET", f"/api/ExportImport/excel/{company_id}", token=ADMIN, raw=True)
check("excel export is xlsx (zip)", s == 200 and xlsx[:2] == b"PK", f"{s}")
s, xlsx2, _ = call("POST", "/api/ExportImport/excel/export-selected", [e1["id"]], ADMIN, raw=True)
check("excel export-selected", s == 200 and xlsx2[:2] == b"PK", f"{s}")
s, b, _ = call("POST", "/api/ExportImport/excel/export-selected", [c2["company"]["id"]], ADMIN, raw=True)
check("export-selected unknown/foreign id rejected", s in (403, 404), f"{s}")
s, html, _ = call("GET", f"/api/ExportImport/html/user/{e1['id']}", token=ADMIN, raw=True)
check("html user export (self-contained)", s == 200 and b"<!doctype html>" in html and b"#15803d" in html, f"{s}")
s, z, _ = call("GET", f"/api/ExportImport/html/{company_id}", token=ADMIN, raw=True)
names = zipfile.ZipFile(io.BytesIO(z)).namelist() if s == 200 else []
check("html company export is zip with index.html", "index.html" in names and len(names) == 5, f"{s} {names}")
s, tpl, _ = call("GET", "/api/ExportImport/excel/template", token=ADMIN, raw=True)
check("excel template", s == 200 and tpl[:2] == b"PK", f"{s}")

# Import: raise limit first, then import the template (1 sample row) + the exported sheet (all duplicates)
call("PUT", f"/api/SuperAdmin/companies/{company_id}/limit", None, SA, data=b"10", content_type="application/json")
body, ctype = multipart({}, tpl, "template.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
s, imp, _ = call("POST", f"/api/ExportImport/excel/import/{company_id}", token=ADMIN, data=body, content_type=ctype)
check("import template row creates 1 user with generated password", s == 200 and imp["created"] == 1 and len(imp["generatedCredentials"]) == 1, f"{s} {imp}")
s, imp2, _ = call("POST", f"/api/ExportImport/excel/import/{company_id}", token=ADMIN, data=body, content_type=ctype)
check("re-import reports row-level duplicate error", s == 200 and imp2["created"] == 0 and imp2["failed"] == 1 and imp2["errors"], f"{s} {imp2}")

s, audit, _ = call("GET", "/api/AuditLog?page=1&pageSize=200", token=ADMIN)
actions = {a["action"] for a in audit["items"]}
check("audit log has LOGIN/CREATE/UPDATE/IMPORT", {"LOGIN", "CREATE", "UPDATE", "IMPORT_EXCEL"} <= actions, f"{actions}")
check("audit never contains password hashes", "passwordHash" not in json.dumps(audit) and "AQAAAA" not in json.dumps(audit))
s, audit2, _ = call("GET", "/api/AuditLog?page=1&pageSize=200", token=ADMIN2)
check("company audit is tenant-scoped", all(a.get("entityId") != e1["id"] for a in audit2["items"]), "")

print("== Admin actions / deactivation")
s, rp, _ = call("POST", f"/api/CompanyAdmin/users/{e1['id']}/reset-password", {"newPassword": None}, ADMIN)
check("reset password returns temporary password", s == 200 and len(rp["newPassword"]) >= 10, f"{s} {rp}")
s, b, _ = call("GET", "/api/Auth/account-info", token=EMP1)
check("old session revoked after reset -> 401", s == 401, f"{s}")
s, b = login("e1@test.local", rp["newPassword"], voen)
check("login with reset password, isFirstLogin=true", s == 200 and b["isFirstLogin"] is True, f"{s} {b}")
s, b, _ = call("PUT", f"/api/CompanyAdmin/users/{e2['id']}/active?isActive=false", token=ADMIN)
check("deactivate employee", s == 200 and b["isActive"] is False)
s, b = login("e2@test.local", "Employee456!", voen)
check("inactive employee login -> 403 ACCOUNT_INACTIVE", s == 403 and b["code"] == "ACCOUNT_INACTIVE", f"{s} {b}")
s, b, _ = call("GET", f"/api/cards/{e2['id']}")
check("inactive employee card -> 410 CARD_DEACTIVATED", s == 410 and b["code"] == "CARD_DEACTIVATED", f"{s} {b}")
s, b, _ = new_user("e5@test.local")
s, comp, _ = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("inactive users still count toward limit", comp["usedCount"] == 5, f"{comp['usedCount']}")
s, b, _ = call("PUT", f"/api/SuperAdmin/companies/{company_id}", {"brandColor": None}, SA)
check("super admin brandColor null resets to default", s == 200 and b["brandColor"] == "#1b4a75", f"{s} {b}")
s, b, _ = call("PUT", f"/api/SuperAdmin/companies/{company_id}/limit", None, SA, data=b"1", content_type="application/json")
check("limit below usage -> 400", s == 400, f"{s} {b}")
s, b, _ = call("PUT", f"/api/SuperAdmin/companies/{company_id}/active?isActive=false", token=SA)
s, b, _ = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("company deactivated -> admin session rejected", s == 401, f"{s}")
s, dash, _ = call("GET", "/api/SuperAdmin/dashboard", token=SA)
check("dashboard", s == 200 and dash["companiesCount"] >= 2 and dash["inactiveCompaniesCount"] >= 1, f"{s} {dash}")
s, lst, _ = call("GET", "/api/SuperAdmin/companies?page=1&pageSize=5", token=SA)
check("companies list paged", s == 200 and "items" in lst and lst["totalCount"] >= 2, f"{s}")

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)

"""
Regression tests for the 7 fixed issues (roles, forced password change, employee limit, clearing profile fields,
VCF/HTML visibility, import credentials, deactivated public cards).

Usage (TEST database only — the script creates companies/users and edits rows directly):
    python scripts/regression_test.py <api-url> <superadmin-email> <superadmin-password>
        --db-container bc-postgres-test --db-user bc_test --db-name bc_test
        [--frontend C:/path/to/business-card-frontend]      # runs the real frontend helpers via esbuild + node
        [--expiry-disabled-url http://localhost:5098]       # same DB, started with Auth__PasswordExpiryDays=0

No secrets are printed.
"""
import argparse
import io
import json
import os
import random
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
import uuid
import zipfile

ap = argparse.ArgumentParser()
ap.add_argument("base")
ap.add_argument("sa_email")
ap.add_argument("sa_password")
ap.add_argument("--db-container")
ap.add_argument("--db-user", default="bc_test")
ap.add_argument("--db-name", default="bc_test")
ap.add_argument("--frontend")
ap.add_argument("--expiry-disabled-url")
args = ap.parse_args()
BASE = args.base.rstrip("/")
RUN = uuid.uuid4().hex[:6]
passed = failed = 0


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  PASS  {name}")
    else:
        failed += 1
        print(f"  FAIL  {name}  {detail}")


def call(method, path, body=None, token=None, raw=False, data=None, content_type=None, base=None):
    headers = {}
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    elif content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request((base or BASE) + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            payload = r.read()
            return r.status, (payload if raw else (json.loads(payload) if payload else None))
    except urllib.error.HTTPError as e:
        payload = e.read()
        try:
            return e.code, json.loads(payload) if payload else None
        except ValueError:
            return e.code, payload.decode(errors="replace")


def login(email, password, voen=None):
    return call("POST", "/api/Auth/login", {"email": email, "password": password, "companyVoen": voen})


def first_login(email, temp, voen, new):
    s, b = login(email, temp, voen)
    assert s == 200, (s, b)
    s2, b2 = call("POST", "/api/Auth/change-password", {"oldPassword": temp, "newPassword": new}, b["accessToken"])
    assert s2 == 200, (s2, b2)
    return b["accessToken"]


def sql(statement):
    if not args.db_container:
        return None
    out = subprocess.run(["docker", "exec", args.db_container, "psql", "-U", args.db_user, "-d", args.db_name,
                          "-At", "-c", statement], capture_output=True, text=True, encoding="utf-8")
    return out.returncode, (out.stdout + out.stderr).strip()


def voen():
    return "".join(random.choice("0123456789") for _ in range(10))


def multipart(file_bytes, filename, mime):
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
            f"Content-Type: {mime}\r\n\r\n").encode() + file_bytes + f"\r\n--{boundary}--\r\n".encode()
    return body, f"multipart/form-data; boundary={boundary}"


def make_xlsx(rows):
    """Minimal .xlsx (inline strings) — first row is the header."""
    def col(i):
        return chr(ord("A") + i)
    def esc(v):
        return str(v).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    sheet_rows = "".join(
        f'<row r="{r + 1}">' + "".join(
            f'<c r="{col(c)}{r + 1}" t="inlineStr"><is><t>{esc(v)}</t></is></c>' for c, v in enumerate(row) if v != "")
        + "</row>" for r, row in enumerate(rows))
    files = {
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Employees" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": f'<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{sheet_rows}</sheetData></worksheet>',
    }
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name, content in files.items():
            z.writestr(name, content)
    return buf.getvalue()


XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
HEADER = ["Ad", "Soyad", "E-poçt", "Şifrə"]

# ------------------------------------------------------------------ frontend helpers (real code via esbuild)
FE = None
if args.frontend:
    tmp = tempfile.mkdtemp(prefix="fe-harness-")
    src = args.frontend.replace("\\", "/") + "/src/utils"
    with open(os.path.join(tmp, "entry.ts"), "w", encoding="utf-8") as f:
        f.write(f'export {{ buildProfileUpdate, getProfileDefaults }} from "{src}/profileEditor";\n'
                f'export {{ buildVcfText, overridesFromContactCard }} from "{src}/vcard";\n'
                f'export {{ getContactCardFields, isContactCardEnabled }} from "{src}/contactCardFields";\n'
                f'export {{ mapEmployee, mapImportResult }} from "{src}/mappers";\n')
    bundle = os.path.join(tmp, "fe.cjs")
    subprocess.run(f'npx --yes esbuild "{os.path.join(tmp, "entry.ts")}" --bundle --platform=node --format=cjs '
                   f'--jsx=automatic --loader:.svg=text --loader:.png=empty --log-level=error '
                   f'--define:import.meta.env={json.dumps(json.dumps({"DEV": False, "VITE_API_BASE_URL": "/api"}))} '
                   f'--outfile="{bundle}"', shell=True, check=True, cwd=args.frontend)
    with open(os.path.join(tmp, "cli.cjs"), "w", encoding="utf-8") as f:
        f.write('const fe = require("./fe.cjs"); let s = ""; process.stdin.on("data", d => s += d).on("end", () => {'
                ' const { script, input } = JSON.parse(s); const out = new Function("fe", "input", script)(fe, input);'
                ' process.stdout.write(JSON.stringify(out)); });')

    def FE(script, data):
        out = subprocess.run(["node", os.path.join(tmp, "cli.cjs")], input=json.dumps({"script": script, "input": data}),
                             capture_output=True, text=True, encoding="utf-8", check=True)
        return json.loads(out.stdout)

# ------------------------------------------------------------------ setup
s, b = login(args.sa_email, args.sa_password)
SA = b["accessToken"]
V1 = voen()
s, comp = call("POST", "/api/SuperAdmin/companies", {"name": "Regression MMC", "voen": V1, "email": f"reg{V1}@test.local", "userLimit": 1}, SA)
CID = comp["company"]["id"]
s, b = login(comp["adminEmail"], comp["adminPassword"], V1)
ADMIN_FIRST = b["accessToken"]

print("== 2. Forced password change / expiry")
s, b = call("PUT", "/api/CompanyAdmin/company", {"name": "Hacked before password change"}, ADMIN_FIRST)
check("first-login admin cannot update company -> 403 PASSWORD_CHANGE_REQUIRED", s == 403 and (b or {}).get("code") == "PASSWORD_CHANGE_REQUIRED", f"{s} {b}")
s, b = call("GET", "/api/CompanyAdmin/company", token=ADMIN_FIRST)
check("first-login admin business reads blocked too", s == 403 and (b or {}).get("code") == "PASSWORD_CHANGE_REQUIRED", f"{s} {b}")
s, b = call("POST", "/api/CompanyAdmin/users", {"companyId": CID, "firstName": "X", "lastName": "Y", "email": f"x{RUN}@t.local", "role": 2}, ADMIN_FIRST)
check("first-login admin cannot create users", s == 403, f"{s} {b}")
s, b = call("GET", "/api/Auth/account-info", token=ADMIN_FIRST)
check("account-info still works and reports PASSWORD_CHANGE_REQUIRED", s == 200 and b["isFirstLogin"] is True and b.get("passwordChangeRequired") == "PASSWORD_CHANGE_REQUIRED", f"{s} {b}")
s, b = call("POST", "/api/Auth/change-password", {"oldPassword": comp["adminPassword"], "newPassword": "RegAdmin123!"}, ADMIN_FIRST)
check("change-password allowed while locked", s == 200, f"{s} {b}")
s, b = call("PUT", "/api/CompanyAdmin/company", {"name": "Regression MMC"}, ADMIN_FIRST)
check("after password change the same token can update the company", s == 200, f"{s} {b}")
s, chk = call("GET", "/api/CompanyAdmin/company", token=ADMIN_FIRST)
check("company name was not changed by the blocked request", chk["name"] == "Regression MMC", chk.get("name"))
ADMIN = ADMIN_FIRST

print("== 1. Roles")
def create(payload, token=ADMIN):
    body = {"companyId": CID, "firstName": "Test", "lastName": "User", "jobTitle": "QA", "password": "Employee123!", "isActive": True}
    body.update(payload)
    return call("POST", "/api/CompanyAdmin/users", body, token)

s, b = create({"email": f"r99{RUN}@t.local", "role": 99})
check("role=99 rejected -> 400 INVALID_ROLE", s == 400 and (b or {}).get("code") == "INVALID_ROLE", f"{s} {b}")
s, b = create({"email": f"r1{RUN}@t.local", "role": 1})
check("company admin cannot create CompanyAdmin -> 403 ROLE_NOT_ALLOWED", s == 403 and (b or {}).get("code") == "ROLE_NOT_ALLOWED", f"{s} {b}")
s, b = create({"email": f"r0{RUN}@t.local", "role": 0})
check("SuperAdmin cannot be created here -> 403", s == 403 and (b or {}).get("code") == "ROLE_NOT_ALLOWED", f"{s} {b}")
s, b = create({"email": f"r99sa{RUN}@t.local", "role": 99}, SA)
check("super admin sending role=99 -> 400 too", s == 400 and (b or {}).get("code") == "INVALID_ROLE", f"{s} {b}")
s, b = create({"email": f"rneg{RUN}@t.local", "role": -1})
check("negative role -> 400", s == 400, f"{s} {b}")
s, page = call("GET", f"/api/CompanyAdmin/users/company/{CID}?page=1&pageSize=1000", token=ADMIN)
check("no account was created by rejected roles", page["totalCount"] == 1, page["totalCount"])
if args.db_container:
    rc, out = sql(f"""INSERT INTO "Users" ("Id","CompanyId","FirstName","LastName","Email","PasswordHash","Role","IsActive","CanEdit","MustChangePassword","PasswordChangedAt","CreatedAt","UpdatedAt")
                      VALUES (gen_random_uuid(),'{CID}','Db','Bypass','db{RUN}@t.local','x',99,true,false,false,now(),now(),now());""")
    check("database rejects role 99 (CK_Users_Role)", rc != 0 and "CK_Users_Role" in out, out[-160:])

print("== 3. Employee limit (Employee role only, inactive included)")
s, e1 = create({"email": f"lim1{RUN}@t.local", "role": 2})
check("limit=1: first employee is created (admin not counted)", s == 200, f"{s} {e1}")
s, b = create({"email": f"lim2{RUN}@t.local", "role": 2})
check("limit=1: second employee rejected", s == 400 and (b or {}).get("code") == "USER_LIMIT_EXCEEDED", f"{s} {b}")
s, c = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("usedCount counts only employees (1)", c.get("usedCount") == 1, c.get("usedCount"))
s, page = call("GET", f"/api/CompanyAdmin/users/company/{CID}?page=1&pageSize=1", token=ADMIN)
check("list still contains the admin (totalCount 2, paged 1)", page["totalCount"] == 2 and len(page["items"]) == 1, page["totalCount"])
call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/active?isActive=false", token=ADMIN)
s, b = create({"email": f"lim3{RUN}@t.local", "role": 2})
check("deactivating the first employee does not free the slot", s == 400 and (b or {}).get("code") == "USER_LIMIT_EXCEEDED", f"{s} {b}")
s, c = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("usedCount still 1 with the employee inactive", c.get("usedCount") == 1, c.get("usedCount"))
call("PUT", f"/api/CompanyAdmin/users/{e1['id']}/active?isActive=true", token=ADMIN)

call("PUT", f"/api/SuperAdmin/companies/{CID}/limit", None, SA, data=b"3", content_type="application/json")
results = []
def parallel_create(i):
    results.append(create({"email": f"par{i}{RUN}@t.local", "role": 2})[0])
threads = [threading.Thread(target=parallel_create, args=(i,)) for i in range(6)]
[t.start() for t in threads]
[t.join() for t in threads]
s, c = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("6 parallel creates with 2 free slots: exactly 2 succeed", sorted(results).count(200) == 2 and c.get("usedCount") == 3, f"{results} used={c['usedCount']}")

call("PUT", f"/api/SuperAdmin/companies/{CID}/limit", None, SA, data=b"5", content_type="application/json")
xlsx = make_xlsx([HEADER] + [[f"Imp{i}", "Par", f"imp{i}{RUN}@t.local", "Employee123!"] for i in range(3)])
mixed = []
def parallel_import():
    body, ctype = multipart(xlsx, "par.xlsx", XLSX)
    mixed.append(("import", call("POST", f"/api/ExportImport/excel/import/{CID}", token=ADMIN, data=body, content_type=ctype)))
def parallel_one(i):
    mixed.append(("create", create({"email": f"mix{i}{RUN}@t.local", "role": 2})))
threads = [threading.Thread(target=parallel_import)] + [threading.Thread(target=parallel_one, args=(i,)) for i in range(2)]
[t.start() for t in threads]
[t.join() for t in threads]
s, c = call("GET", "/api/CompanyAdmin/company", token=ADMIN)
check("parallel import + creates never exceed the limit (5)", c.get("usedCount") == 5, f"used={c['usedCount']}")

print("== 4. Clearing profile fields")
call("PUT", f"/api/SuperAdmin/companies/{CID}/limit", None, SA, data=b"50", content_type="application/json")
s, emp = create({"email": f"clr{RUN}@t.local", "role": 2})
filled = {"phone1": "+994501112233", "phone2": "+994551112233", "whatsappPhone": "+994701112233",
          "linkedinUrl": "https://linkedin.com/in/x", "facebookUrl": "https://facebook.com/x",
          "instagramUrl": "https://www.instagram.com/x", "googleMapsUrl": "https://maps.google.com/?q=1",
          "address": "Bakı, Nizami küç. 1", "birthday": "1990-05-04", "photoUrl": "data:image/png;base64,iVBORw0KGgo=",
          "socialAccounts": [{"platformName": "telegram", "profileUrl": "https://t.me/x"}],
          "contactInfos": [{"contactType": "phone", "value": "+994121112233", "label": "Ofis", "displayOrder": 0}]}
s, b = call("PUT", f"/api/CompanyAdmin/users/{emp['id']}", filled, ADMIN)
check("profile filled", s == 200 and b["phone2"] == filled["phone2"] and len(b["socialAccounts"]) == 1, f"{s}")
s, before = call("GET", f"/api/User/{emp['id']}", token=ADMIN)
if FE:
    payload = FE("""
        const e = fe.mapEmployee(input);
        const v = fe.getProfileDefaults(e);
        for (const k of ["phone2","whatsappPhone","linkedinUrl","facebookUrl","instagramUrl","googleMapsUrl"]) v[k] = "";
        return fe.buildProfileUpdate(v, { address: "", birthday: "", photoUrl: "", cardBackgroundUrl: "",
                                          contactInfos: [], socialAccounts: [] });""", before)
    check("frontend payload sends cleared fields as empty strings", all(payload.get(k) == "" for k in
          ["phone2", "whatsappPhone", "linkedinUrl", "facebookUrl", "instagramUrl", "googleMapsUrl", "address", "birthday"]), json.dumps(payload)[:300])
else:
    payload = {k: "" for k in ["phone2", "whatsappPhone", "linkedinUrl", "facebookUrl", "instagramUrl", "googleMapsUrl", "address", "birthday", "photoUrl"]}
    payload.update({"contactInfos": [], "socialAccounts": []})
s, b = call("PUT", f"/api/CompanyAdmin/users/{emp['id']}", payload, ADMIN)
s, after = call("GET", f"/api/User/{emp['id']}", token=ADMIN)
cleared = ["phone2", "whatsappPhone", "linkedinUrl", "facebookUrl", "instagramUrl", "googleMapsUrl", "address", "birthday", "photoUrl"]
check("cleared fields are null after GET", all(after[k] is None for k in cleared), {k: after[k] for k in cleared if after[k] is not None})
check("empty arrays clear contacts and socials", after["contactInfos"] == [] and after["socialAccounts"] == [], f"{after['contactInfos']} {after['socialAccounts']}")
check("untouched phone1 kept", after["phone1"] == filled["phone1"], after["phone1"])
s, b = call("PUT", f"/api/CompanyAdmin/users/{emp['id']}", {"jobTitle": "Lead"}, ADMIN)
check("omitted fields keep their value", b["jobTitle"] == "Lead" and b["phone1"] == filled["phone1"], f"{b['jobTitle']} {b['phone1']}")

print("== 5. VCF / HTML match the visible card")
hidden = json.dumps(["core:phone1", "contact:phone:+994 12 999 00 00", "social:website::Gizli:https://hidden.example"])
card_user = {
    "firstName": "Əli", "lastName": "Həsənov", "jobTitle": "Satış, menecer; baş", "phone1": "+994501111111",
    "phone2": "+994552222222", "address": "Bakı, Nizami küç. 5", "googleMapsUrl": "https://maps.google.com/?q=baku",
    "birthday": "1988-02-03", "additionalInfo": "Sətir 1\nSətir 2",
    "contactInfos": [
        {"contactType": "phone", "value": "+994 12 555 66 77", "label": "Ofis", "displayOrder": 0},
        {"contactType": "phone", "value": "+994 12 999 00 00", "label": "Gizli telefon", "displayOrder": 1},
        {"contactType": "email", "value": "satis@example.az", "label": "Satış", "displayOrder": 2},
        {"contactType": "phone", "value": "Kontaktı Yüklə", "label": "__setclapp_contact_card__:buttonLabel", "displayOrder": 3},
        {"contactType": "phone", "value": "Əli Həsənov; Jr, test", "label": "__setclapp_contact_card__:name", "displayOrder": 4},
        {"contactType": "phone", "value": "https://ali.example.az", "label": "__setclapp_contact_card__:website", "displayOrder": 5},
        {"contactType": "phone", "value": hidden, "label": "__setclapp_disabled_links__:", "displayOrder": 6},
    ],
    "socialAccounts": [
        {"platformName": "website::Şirkət saytı", "profileUrl": "https://company.example.az"},
        {"platformName": "website::Gizli", "profileUrl": "https://hidden.example"},
    ],
}
s, cu = create({"email": f"vcf{RUN}@t.local", "role": 2})
s, b = call("PUT", f"/api/CompanyAdmin/users/{cu['id']}", card_user, ADMIN)
check("card user saved", s == 200, f"{s} {b}")
s, vcf_bytes = call("GET", f"/api/ExportImport/vcf/{cu['id']}", token=ADMIN, raw=True)
vcf = vcf_bytes.decode("utf-8")
unfolded = vcf.replace("\r\n ", "")
check("VCF is UTF-8 with Azerbaijani letters", "Həsənov" in unfolded and "Satış" in unfolded, unfolded[:200])
check("hidden core phone1 not exported", "501111111" not in unfolded)
check("hidden extra phone not exported", "999 00 00" not in unfolded)
check("hidden website not exported", "hidden.example" not in unfolded)
check("visible phone2 exported", "TEL;TYPE=CELL,VOICE:+994552222222" in unfolded)
check("extra labeled phone exported with label", "TEL;TYPE=VOICE:+994 12 555 66 77" in unfolded and "X-ABLabel:Ofis" in unfolded)
check("extra email exported with label", "EMAIL;TYPE=INTERNET:satis@example.az" in unfolded and "X-ABLabel:Satış" in unfolded)
check("contact card name used for FN (escaped)", "FN:Əli Həsənov\\; Jr\\, test" in unfolded, [l for l in unfolded.split("\r\n") if l.startswith("FN")])
check("contact card website exported", "URL:https://ali.example.az" in unfolded and "X-ABLabel:Vebsayt" in unfolded)
check("visible website exported", "URL:https://company.example.az" in unfolded and "X-ABLabel:Şirkət saytı" in unfolded)
check("no __setclapp metadata in VCF", "__setclapp" not in unfolded and "Kontaktı Yüklə" not in unfolded)
check("TITLE escaped and NOTE newline encoded", "TITLE:Satış\\, menecer\\; baş" in unfolded and "NOTE:Sətir 1\\nSətir 2" in unfolded)
check("VCF lines folded at <= 75 octets", all(len(l.encode("utf-8")) <= 75 for l in vcf.split("\r\n")))
if FE:
    s, full = call("GET", f"/api/User/{cu['id']}", token=ADMIN)
    fe_vcf = FE("""
        const e = fe.mapEmployee(input);
        const f = fe.getContactCardFields(e.contactInfos);
        return fe.buildVcfText(e, fe.overridesFromContactCard(f, fe.isContactCardEnabled(e.contactInfos)));""", full)
    check("backend VCF equals the card's own VCF (frontend buildVcfText)", fe_vcf.strip() == vcf.strip(),
          "\nFE:\n" + fe_vcf[:600] + "\nBE:\n" + vcf[:600])
s, html_bytes = call("GET", f"/api/ExportImport/html/user/{cu['id']}", token=ADMIN, raw=True)
html = html_bytes.decode("utf-8")
check("HTML excludes hidden phone/contact/website and metadata",
      "501111111" not in html and "999 00 00" not in html and "hidden.example" not in html and "__setclapp" not in html)
check("HTML includes visible rows and labels", "+994552222222" in html and "Ofis" in html and "satis@example.az" in html and "company.example.az" in html and "Həsənov" in html)
check("HTML offers the contact-card vCard offline", "data:text/vcard;charset=utf-8;base64," in html)
s, z = call("GET", f"/api/ExportImport/html/{CID}", token=ADMIN, raw=True)
archive = zipfile.ZipFile(io.BytesIO(z))
entry = [n for n in archive.namelist() if "Həsənov" in n or "H%C9%99s%C9%99nov" in n]
zhtml = archive.read(entry[0]).decode("utf-8") if entry else ""
check("ZIP export uses the same visible model", bool(entry) and "501111111" not in zhtml and "Ofis" in zhtml, archive.namelist())

print("== 6. Import credentials")
dup_email = f"vcf{RUN}@t.local"
xlsx = make_xlsx([HEADER, ["Nigar", "Quliyeva", f"gen{RUN}@t.local", ""], ["Dup", "Row", dup_email, ""],
                  ["Own", "Pass", f"own{RUN}@t.local", "OwnPass123!"]])
body, ctype = multipart(xlsx, "credentials.xlsx", XLSX)
s, res = call("POST", f"/api/ExportImport/excel/import/{CID}", token=ADMIN, data=body, content_type=ctype)
creds = res.get("generatedCredentials", []) if isinstance(res, dict) else []
check("partial import: created 2, failed 1", s == 200 and res["created"] == 2 and res["failed"] == 1, f"{s} {res and {k: v for k, v in res.items() if k != 'generatedCredentials'}}")
check("credentials only for the created row without a password", len(creds) == 1 and creds[0]["email"] == f"gen{RUN}@t.local" and creds[0]["row"] == 2, [c["email"] for c in creds])
if creds:
    s, b = login(creds[0]["email"], creds[0]["password"], V1)
    check("login with the generated password works (must change password)", s == 200 and b["isFirstLogin"] is True, f"{s}")
if FE:
    mapped = FE("return fe.mapImportResult(input);", res)
    check("frontend mapImportResult keeps generatedCredentials", mapped and len(mapped["generatedCredentials"]) == 1 and mapped["generatedCredentials"][0]["email"] == f"gen{RUN}@t.local", mapped and mapped.get("generatedCredentials"))
s, audit = call("GET", "/api/AuditLog?page=1&pageSize=200", token=ADMIN)
check("generated password never written to the audit log", not creds or creds[0]["password"] not in json.dumps(audit))
s, tpl = call("GET", "/api/ExportImport/excel/template", token=ADMIN, raw=True)
body, ctype = multipart(tpl, "template.xlsx", XLSX)
s, res2 = call("POST", f"/api/ExportImport/excel/import/{CID}", token=ADMIN, data=body, content_type=ctype)
tcreds = res2.get("generatedCredentials", []) if isinstance(res2, dict) else []
if tcreds:
    s, b = login(tcreds[0]["email"], tcreds[0]["password"], V1)
check("standard template with empty password returns usable credentials", len(tcreds) == 1 and s == 200, f"{res2 and res2.get('created')} {s}")

print("== 7. Public card states")
call("PUT", f"/api/CompanyAdmin/users/{cu['id']}/nickname", {"nickname": f"reg-card{RUN}"}, ADMIN)
def scans():
    return call("GET", f"/api/Analytics/scans/count?companyId={CID}", token=ADMIN)[1]["count"]
n0 = scans()
s, b = call("GET", f"/api/cards/{cu['id']}?source=nfc")
check("active card opens", s == 200 and b["id"] == cu["id"], f"{s}")
n1 = scans()
call("PUT", f"/api/CompanyAdmin/users/{cu['id']}/active?isActive=false", token=ADMIN)
s, b = call("GET", f"/api/cards/{cu['id']}?source=nfc")
check("deactivated employee -> 410 CARD_DEACTIVATED without profile data",
      s == 410 and (b or {}).get("code") == "CARD_DEACTIVATED" and set(b) == {"code", "message"}, f"{s} {b}")
s, b = call("GET", f"/api/cards/reg-card{RUN}")
check("deactivated employee by nickname -> 410", s == 410 and (b or {}).get("code") == "CARD_DEACTIVATED", f"{s} {b}")
call("PUT", f"/api/CompanyAdmin/users/{cu['id']}/active?isActive=true", token=ADMIN)
call("PUT", f"/api/SuperAdmin/companies/{CID}/active?isActive=false", token=SA)
s, b = call("GET", f"/api/cards/{cu['id']}?source=qr")
check("deactivated company -> 410 CARD_DEACTIVATED", s == 410 and (b or {}).get("code") == "CARD_DEACTIVATED", f"{s} {b}")
call("PUT", f"/api/SuperAdmin/companies/{CID}/active?isActive=true", token=SA)
s, b = call("GET", f"/api/cards/{uuid.uuid4()}")
check("unknown UUID -> 404 CARD_NOT_FOUND", s == 404 and (b or {}).get("code") == "CARD_NOT_FOUND" and set(b) == {"code", "message"}, f"{s} {b}")
s, b = call("GET", "/api/cards/no-such-nickname-zz")
check("unknown nickname -> 404 CARD_NOT_FOUND", s == 404 and (b or {}).get("code") == "CARD_NOT_FOUND", f"{s} {b}")
check("only the successful open was counted as a scan", n1 == n0 + 1 and scans() == n1, f"{n0} {n1} {scans()}")

print("== 2b. Expired password")
s, exp_user = create({"email": f"exp{RUN}@t.local", "role": 2})
EXP = first_login(f"exp{RUN}@t.local", "Employee123!", V1, "Expire123!")
s, b = call("PUT", "/api/User/profile/nickname", {"nickname": f"canedit-false{RUN}"}, EXP)
check("normal state: canEdit=false employee can still set own nickname", s == 200, f"{s} {b}")
if args.db_container:
    sql(f"""UPDATE "Users" SET "PasswordChangedAt" = now() - interval '100 days' WHERE "Email" = 'exp{RUN}@t.local';""")
    s, b = call("GET", f"/api/User/{exp_user['id']}", token=EXP)
    check("expired password blocks business reads -> 403 PASSWORD_EXPIRED", s == 403 and (b or {}).get("code") == "PASSWORD_EXPIRED", f"{s} {b}")
    s, b = call("PUT", "/api/User/profile/nickname", {"nickname": f"blocked{RUN}"}, EXP)
    check("expired password blocks nickname change", s == 403 and (b or {}).get("code") == "PASSWORD_EXPIRED", f"{s} {b}")
    s, b = call("GET", "/api/Auth/account-info", token=EXP)
    check("account-info reports expiry (days <= 0)", s == 200 and (b.get("daysUntilPasswordExpiry") or 1) <= 0 and b.get("passwordChangeRequired") == "PASSWORD_EXPIRED", f"{s} {b and b.get('daysUntilPasswordExpiry')}")
    if args.expiry_disabled_url:
        s, b = call("GET", f"/api/User/{exp_user['id']}", token=EXP, base=args.expiry_disabled_url)
        check("PasswordExpiryDays=0 disables the expiry block", s == 200, f"{s} {b}")
        s, b = call("GET", "/api/Auth/account-info", token=EXP, base=args.expiry_disabled_url)
        check("PasswordExpiryDays=0: no days until expiry", s == 200 and b["daysUntilPasswordExpiry"] is None, f"{s}")
    s, b = call("POST", "/api/Auth/change-password", {"oldPassword": "Expire123!", "newPassword": "Renewed123!"}, EXP)
    s, b = call("GET", f"/api/User/{exp_user['id']}", token=EXP)
    check("after changing the expired password requests work again", s == 200, f"{s} {b}")

print(f"\n{passed} passed, {failed} failed")
raise SystemExit(1 if failed else 0)

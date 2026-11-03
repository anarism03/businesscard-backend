# BusinessCard Backend (C# / ASP.NET Core 10 + PostgreSQL)

Swagger-dəki `BusinessCard API` müqaviləsinə uyğun yazılmış yeni backend. Frontendin istifadə etdiyi bütün endpointlər + 4 əlavə tapşırıq:

1. **Şirkət brend rəngi** (`brandColor`) bazada saxlanır, public kartda qaytarılır.
2. **Skan vaxtı** serverdə UTC ilə yaranır (`timestamptz`), API `...Z` formatında qaytarır.
3. **Şəkil crop / recrop** — EXIF istiqaməti düzəldilir, orijinal gizli saxlanır.
4. **Nickname** — optional, bütün şirkətlər üzrə unikal; UUID/NFC linkləri işləməyə davam edir.

---

## 1. Lazım olanlar

| Proqram | Qeyd |
|---|---|
| .NET 10 SDK | `dotnet --version` → 10.x |
| PostgreSQL 15+ | Aşağıdakı **A** (Docker) və ya **B** (Windows installer) variantı |
| VS Code + **C# Dev Kit** | Kodu açmaq üçün (məcburi deyil) |

## 2. PostgreSQL

### A) Docker ilə (tövsiyə — Docker Desktop artıq var)

Bu qovluqda `.env` faylı yarat (git-ə düşmür):

```
POSTGRES_PASSWORD=ÖZ_GÜCLÜ_ŞİFRƏN
```

Sonra:

```bash
docker compose up -d
```

Baza `localhost:5432`, istifadəçi `businesscard_app`, baza adı `businesscard_db`. Məlumatlar `businesscard-pgdata` volume-da qalır (kompüter sönsə də itmir).

### B) Windows installer ilə

1. https://www.postgresql.org/download/windows/ → installer → PostgreSQL Server + Command Line Tools (pgAdmin artıq var). Port `5432`.
2. pgAdmin → **Login/Group Roles → Create**: `businesscard_app`, şifrə, *Can login* = aktiv.
3. **Databases → Create**: `businesscard_db`, Owner = `businesscard_app`.

## 3. Gizli konfiqurasiya (user-secrets)

Terminalı `src/BusinessCard.Api` qovluğunda aç:

```bash
cd src/BusinessCard.Api
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=businesscard_db;Username=businesscard_app;Password=ÖZ_GÜCLÜ_ŞİFRƏN"
dotnet user-secrets set "Jwt:Key" "BURAYA_ƏN_AZI_32_SİMVOLLU_TƏSADÜFİ_AÇAR"
dotnet user-secrets set "Seed:SuperAdminEmail" "superadmin@sirket.az"
dotnet user-secrets set "Seed:SuperAdminPassword" "SuperAdmin_Şifrəsi_123"
```

Təsadüfi JWT açarı yaratmaq üçün (PowerShell):

```powershell
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

Şifrələr kompüterində `%APPDATA%\Microsoft\UserSecrets\businesscard-api-7d3f2c1e\secrets.json`-da qalır, git-ə və frontendə düşmür.

## 4. Cədvəlləri yarat

```bash
dotnet tool restore
dotnet ef database update --project src/BusinessCard.Api
```

pgAdmin-də: `businesscard_db → Schemas → public → Tables` (Companies, Users, ContactInfos, SocialAccounts, ScanEvents, AuditLogs, StoredImages).

## 5. Backendi işə sal

```bash
cd src/BusinessCard.Api
dotnet run
```

- API: http://localhost:5000
- Swagger: http://localhost:5000/swagger
- İlk işə düşəndə `Seed:*` dəyərlərindən **superadmin** yaradılır (artıq varsa heç nə etmir). Superadmin girişində VÖEN boş qalır.

Dayandırmaq: `Ctrl+C`.

## 6. Frontendi bağla

`business-card-frontend/.env.local`:

```
VITE_API_BASE_URL=/api
VITE_API_PROXY_TARGET=http://localhost:5000
```

```bash
npm run dev
```

Vite `/api` və `/uploads` sorğularını backendə yönləndirir. `.env.local` dəyişəndə `npm run dev`-i yenidən başlat.

## 7. Yoxlama (smoke test)

Backend işləyərkən (superadmin məlumatları ilə):

```bash
python scripts/smoke_test.py http://localhost:5000 superadmin@sirket.az "SuperAdmin_Şifrəsi_123"
```

96 yoxlama: rollar, tenant izolyasiyası, limit, brandColor, nickname (409/400, paralel sorğu), skan vaxtı, crop/recrop, export/import, audit. Test öz şirkətlərini yaradır — **canlı bazada yox, test bazasında** işlət. Login rate-limit (10/dəq) səbəbindən iki testi arxa-arxaya işlətmə.

---

## Qərarlar və davranış (orijinal backenddə sənədləşməmiş hissələr)

Bunlar orijinal kodun bərpası **deyil**, seçilmiş davranışdır; `appsettings.json`-da dəyişmək olar:

| Mövzu | Seçim |
|---|---|
| Login | Şirkət istifadəçiləri: e-poçt + şifrə + VÖEN. Superadmin: VÖEN boş. Yanlış giriş → `401 INVALID_CREDENTIALS`, deaktiv → `403 ACCOUNT_INACTIVE` |
| Şifrə müddəti | `Auth:PasswordExpiryDays = 90` (0 = söndür). İlk girişdə və admin sıfırlayandan sonra şifrə dəyişmək məcburidir |
| E-poçt unikallığı | Şirkət daxilində unikal (fərqli şirkətlərdə eyni e-poçt ola bilər) |
| Limit | Bütün `Employee` rollu istifadəçilər sayılır (aktiv + deaktiv). Şirkət admini sayılmır |
| Yeni işçi | `canEdit = false`. Şifrə göndərilməsə müvəqqəti şifrə yaradılır və cavabda **bir dəfə** qaytarılır |
| Skan | Hər `GET /api/cards/{id}` bir skandır. Eyni cihazdan 30 saniyə ərzində təkrar açılış bir dəfə sayılır (`Analytics:DuplicateWindowSeconds`). Qrafik günləri Asia/Baku saatı ilə qruplaşdırılır |
| Offline QR | Frontend QR-a vCard yazır — telefon bunu internetsiz oxuyur, API-yə sorğu getmir, **skan sayılmır**. Bu backenddən asılı deyil |
| Admin şifrə sıfırlaması / deaktivasiya | İstifadəçinin bütün açıq sessiyaları etibarsız olur |
| Audit | Şirkət/istifadəçi/kontakt dəyişiklikləri avtomatik yazılır; şifrə hash-ləri və şəkil baytları heç vaxt yazılmır |
| Şəkillər | Nəticə: `wwwroot/uploads/...` (public). Orijinal: `storage/originals` (yalnız icazəli istifadəçi). Hər iki qovluğu backup et |

### Crop müqaviləsi

`multipart/form-data`: `file`, `x`, `y`, `width`, `height`, `sourceWidth`, `sourceHeight` (altısı birlikdə və ya heç biri).
Koordinatlar **EXIF istiqaməti düzəldilmiş orijinal** şəklin pikselləridir, başlanğıc sol yuxarı küncdür.
Cavab: `imageId`, `url`, `originalUrl`, ölçülər, `crop`. Yenidən kəsmək: `PUT /api/images/{imageId}/crop` (JSON eyni 6 sahə) — həmişə orijinaldan kəsilir.

| Endpoint | Kim |
|---|---|
| `POST /api/User/profile/photo`, `/card-background` | Özü (işçi üçün `canEdit` lazımdır) |
| `POST /api/CompanyAdmin/users/{id}/photo`, `/card-background` | Şirkət admini (yalnız öz şirkəti) |
| `POST /api/CompanyAdmin/company/logo` | Şirkət admini |

### Swagger-də olub, frontend istifadə etmədiyi üçün yaradılmayanlar

`GET /api/CompanyAdmin/users/{id}/nfc-link`, `GET /api/ExportImport/qr/{userId}`, `GET /api/cards/{id}/vcf`, `GET /api/cards/{id}/qr`, `POST /api/User/profile/social-icon` — frontend QR/VCF/linkləri özü yaradır.

## Production qeydləri

- `Jwt:Key`, connection string — mühit dəyişənləri ilə (`ConnectionStrings__DefaultConnection`, `Jwt__Key`).
- `App:PublicCardBaseUrl` — real domen (məs. `https://card.sirket.az/v/`), VCF-dəki link üçün.
- `Cors:AllowedOrigins` — frontend domeni (eyni domendən reverse-proxy ilə işləyirsə lazım deyil).
- Frontendin `nginx.conf`-una `/api` və `/uploads` üçün backendə `proxy_pass` əlavə olunmalıdır.
- `wwwroot/uploads` və `storage` qovluqları davamlı diskdə (volume) olmalıdır.

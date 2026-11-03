using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using BusinessCard.Api.Domain;

namespace BusinessCard.Api.Services;

/// <summary>Images already resolved to data URIs for the offline card.</summary>
public sealed record CardHtmlImages(string? Photo, string? Background, string? Logo, string? VcfHref);

/// <summary>
/// Offline HTML copy of the public card page (<c>BusinessCardPublicView</c> + <c>index.css</c>/<c>appearance.css</c>).
/// Same layout, sections, tiles, brand palette (<c>buildAppearance</c>), light/dark mode and footer.
/// No scripts: grouped links open through <c>:target</c> and the theme toggle is a checkbox.
/// </summary>
public static class CardHtmlRenderer
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    // ------------------------------------------------------------------ assets (copied from the frontend public/ folder)

    private static readonly ConcurrentDictionary<string, string?> AssetCache = new();

    private static string? Asset(string path) => AssetCache.GetOrAdd(path, static p =>
    {
        var assembly = typeof(CardHtmlRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream("CardAssets/" + p)
                           ?? assembly.GetManifestResourceStream("CardAssets/" + p.Replace('/', '\\'));
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(ms.ToArray());
    });

    private static readonly Dictionary<string, string> PlatformIcons = new()
    {
        ["contact-card"] = "contacts", ["phone"] = "phone", ["email"] = "mail", ["messenger"] = "messenger",
        ["whatsapp"] = "whatsapp", ["whatsapp-business"] = "whatsapp-business", ["sms"] = "sms",
        ["telegram"] = "telegram", ["viber"] = "viber", ["zoom"] = "zoom", ["skype"] = "skype",
        ["facebook"] = "facebook", ["instagram"] = "instagram", ["linkedin"] = "linkedin", ["tiktok"] = "tiktok",
        ["youtube"] = "youtube", ["snapchat"] = "snapchat", ["threads"] = "threads", ["twitch"] = "twitch-tv",
        ["vk"] = "vk", ["wechat"] = "wechat", ["x"] = "twitter_new", ["github"] = "github", ["discord"] = "discord",
        ["slack"] = "slack", ["spotify"] = "spotify", ["applemusic"] = "applemusic", ["youtube-music"] = "youtube_music",
        ["googlemaps"] = "maps", ["amazon"] = "amazon", ["lalafo"] = "lalafoaz", ["tapaz"] = "tapaz",
    };

    private static readonly ConcurrentDictionary<string, string> AntdCache = new();

    /// <summary>Ant Design outlined icon (the same SVG the React card renders), sized by font-size.</summary>
    private static string Svg(string name, string cls = "ico") => AntdCache.GetOrAdd(name, static n =>
    {
        var assembly = typeof(CardHtmlRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream($"CardAssets/antd/{n}.svg")
                           ?? assembly.GetManifestResourceStream($"CardAssets/antd\\{n}.svg")
                           ?? throw new InvalidOperationException($"Missing icon {n}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }).Replace("<svg ", $"<svg class=\"{cls}\" fill=\"currentColor\" aria-hidden=\"true\" ");

    /// <summary>Brand-coloured icon exactly like <c>BrandIcon</c>: the SVG is used as a mask over <c>--color-brand</c>.</summary>
    private static string Icon(VisibleLinkRow row, string cls)
    {
        var src = row.Platform is { } p && PlatformIcons.TryGetValue(p, out var name) ? Asset($"icons/{name}.svg") : null;
        return src is not null
            ? $"<span class=\"{cls}\" aria-hidden=\"true\" style=\"-webkit-mask-image:url({src});mask-image:url({src})\"></span>"
            : $"<span class=\"{cls}-fallback\">{Svg("global")}</span>";
    }

    // ------------------------------------------------------------------ palette (utils/appearance.ts buildAppearance)

    private static int[] Rgb(string hex) => [Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16)];

    private static string Mix(string first, string second, double amount)
    {
        var a = Rgb(first);
        var b = Rgb(second);
        return "#" + string.Concat(a.Select((c, i) => ((int)Math.Floor(c + (b[i] - c) * amount + 0.5)).ToString("x2")));
    }

    private static double Luminance(string hex)
    {
        double[] w = [0.2126, 0.7152, 0.0722];
        return Rgb(hex).Select((c, i) =>
        {
            var v = c / 255.0;
            return (v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)) * w[i];
        }).Sum();
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static string Readable(string color, string[] backgrounds, double minimum)
    {
        if (backgrounds.All(bg => Contrast(color, bg) >= minimum)) return color;
        var target = Luminance(backgrounds[0]) > 0.35 ? "#000000" : "#ffffff";
        for (var step = 1; step <= 100; step++)
        {
            var candidate = Mix(color, target, step / 100.0);
            if (backgrounds.All(bg => Contrast(candidate, bg) >= minimum)) return candidate;
        }
        return target;
    }

    private static string NormalizeHex(string? value)
    {
        var hex = (value ?? "").Trim().TrimStart('#').ToLowerInvariant();
        if (hex.Length == 3 && hex.All(Uri.IsHexDigit)) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex : Company.DefaultBrandColor;
    }

    private static string Palette(string raw, bool dark)
    {
        var surface = dark ? "#171e2b" : "#ffffff";
        var canvas = dark ? "#0c111b" : "#f3f5f8";
        var elevated = dark ? "#202938" : "#ffffff";
        var muted = dark ? "#1c2533" : "#f7f8fa";
        var soft = Mix(surface, raw, dark ? 0.18 : 0.075);
        var primary = Readable(raw, [surface, soft, muted, elevated, canvas], dark ? 7 : 4.5);
        var action = Readable(Mix(dark ? Readable(raw, [surface], 3) : raw, "#ffffff", 0.06), ["#ffffff"], 4.5);
        var vars = new (string, string)[]
        {
            ("--color-brand", raw), ("--color-primary", primary), ("--color-primary-soft", soft),
            ("--color-contact-action", action), ("--color-contact-action-hover", Mix(action, "#000000", 0.04)),
            ("--color-contact-action-active", Mix(action, "#000000", 0.08)),
            ("--color-surface", surface), ("--color-surface-muted", muted), ("--color-surface-subtle", muted),
            ("--color-avatar-muted", dark ? "#2a3547" : "#eef1f6"), ("--color-canvas", canvas),
            ("--color-text", dark ? "#f1f5f9" : "#0f172a"), ("--color-text-secondary", dark ? "#bcc6d5" : "#586477"),
            ("--color-text-muted", dark ? "#9aa9bf" : "#647185"), ("--color-border", dark ? "#303c50" : "#e2e6ed"),
            ("--color-overlay", dark ? "rgba(0,0,0,0.65)" : "rgba(15,23,42,0.48)"),
            ("--shadow-sm", dark ? "0 2px 8px rgba(0,0,0,0.2)" : "0 1px 2px rgba(15,23,42,0.04), 0 4px 12px rgba(15,23,42,0.03)"),
            ("--shadow-lg", dark ? "0 18px 60px rgba(0,0,0,0.4)" : "0 8px 20px rgba(15,23,42,0.08), 0 30px 70px rgba(15,23,42,0.13)"),
            ("--card-accent", raw), ("--card-accent-deep", Mix(raw, "#000000", 0.3)), ("--card-accent-light", Mix(raw, "#ffffff", 0.24)),
            ("--footer-bg", dark ? canvas : "linear-gradient(180deg, rgba(255,255,255,0.9), #ffffff)"),
            ("--logo-light", dark ? "none" : "block"), ("--logo-dark", dark ? "block" : "none"),
            ("--icon-moon", dark ? "none" : "block"), ("--icon-sun", dark ? "block" : "none"),
            ("color-scheme", dark ? "dark" : "light"),
        };
        return string.Concat(vars.Select(v => $"{v.Item1}:{v.Item2};"));
    }

    // ------------------------------------------------------------------ styles (index.css, appearance.css, *.styles.ts)

    private const string Css = """
*,*::before,*::after{box-sizing:border-box}
html,body{margin:0}
body{min-height:100dvh;font-family:"Poppins",system-ui,sans-serif;-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale;color:var(--color-text);background:var(--color-canvas);overflow-x:hidden;line-height:1.5}
a{-webkit-tap-highlight-color:transparent}
.page{width:100%;min-height:100dvh;display:flex;flex-direction:column;align-items:center;background:var(--color-canvas)}
.frame{position:relative;width:min(100%,384px);background:var(--color-surface);display:flex;flex-direction:column;flex:1}
@media (min-width:420px){.frame{box-shadow:0 0 0 1px rgba(15,23,42,.04),var(--shadow-lg)}}
.card{--tile:62px;--tile-r:15px;--tile-img:28px;--tile-fb:24px;--tile2:38px;--tile2-r:10px;position:relative;isolation:isolate;width:100%;flex:1;display:flex;flex-direction:column;overflow:hidden;background:var(--color-surface);color:var(--color-text);font-size:14px}
@media (min-width:768px){.card{--tile:51.15px;--tile-r:12.4px;--tile-img:23.1px;--tile-fb:19.8px;--tile2:31.35px;--tile2-r:8.25px}}
.cover{position:relative;width:100%;aspect-ratio:16/9;margin:0;display:flex;align-items:center;justify-content:center;overflow:clip;isolation:isolate;box-shadow:inset 0 0 0 1px rgba(15,23,42,.06);background:linear-gradient(145deg,var(--color-surface-subtle) 0%,var(--color-surface) 48%,var(--color-surface-subtle) 100%)}
.cover--accent{background:linear-gradient(145deg,var(--card-accent-deep) 0%,var(--card-accent) 58%,var(--card-accent-light) 100%)}
.cover--logo{background:transparent}
.cover::before{content:"";position:absolute;inset:0;z-index:1;pointer-events:none;background:linear-gradient(180deg,rgba(15,23,42,.02) 0%,transparent 46%),linear-gradient(0deg,rgba(15,23,42,.1) 0%,transparent 42%)}
.cover-media{position:absolute;inset:0;z-index:1;width:100%;height:100%;object-fit:contain;object-position:center}
.cover--logo .cover-media{padding:48px 8% 56px}
.cover-pattern{position:absolute;inset:0;opacity:.18;background-image:radial-gradient(rgba(255,255,255,.8) 1px,transparent 1px);background-size:18px 18px}
.theme-toggle{position:absolute;top:12px;right:12px;z-index:3;display:inline-flex;align-items:center;justify-content:center;width:36px;height:36px;border:1px solid var(--color-border);border-radius:11px;background:var(--color-surface);color:var(--color-primary);font-size:17px;cursor:pointer;transition:background-color .18s ease,border-color .18s ease}
.theme-toggle:hover{background:var(--color-primary-soft);border-color:var(--color-primary)}
.theme-toggle .moon{display:var(--icon-moon)}.theme-toggle .sun{display:var(--icon-sun)}
.ico{width:1em;height:1em;display:block}
.profile{position:relative;display:flex;flex-direction:column;align-items:center;padding:0 18px;text-align:center;animation:enter .42s cubic-bezier(.22,1,.36,1) .05s both}
.avatar-wrap{position:relative;z-index:2;width:110px;height:110px;margin-top:-44px}
.avatar{width:100%;height:100%;display:flex;align-items:center;justify-content:center;border-radius:50%;overflow:hidden;border:4px solid var(--color-surface);background:var(--color-avatar-muted);box-shadow:0 12px 30px rgba(15,23,42,.16),0 0 0 1px color-mix(in srgb,var(--color-primary) 10%,transparent),0 0 0 5px var(--color-primary-soft),0 0 0 7px color-mix(in srgb,var(--color-primary) 8%,transparent);transition:transform .32s cubic-bezier(.22,1,.36,1),box-shadow .32s cubic-bezier(.22,1,.36,1)}
.avatar-wrap:hover .avatar{transform:translateY(-2px) scale(1.012);box-shadow:0 16px 36px rgba(15,23,42,.18),0 0 0 1px color-mix(in srgb,var(--color-primary) 10%,transparent),0 0 0 5px var(--color-primary-soft),0 0 0 7px color-mix(in srgb,var(--color-primary) 8%,transparent)}
.avatar img{width:100%;height:100%;object-fit:cover}
.avatar-fallback{font-size:38px;font-weight:600;color:var(--color-primary)}
h1{margin:10px 0 2px;color:var(--color-text);font-size:20.5px;line-height:1.12;font-weight:700;letter-spacing:-.032em;overflow-wrap:anywhere}
.role{margin:2px 0 0;color:var(--color-text-secondary);font-size:13.5px;line-height:1.4;overflow-wrap:anywhere}
.content{position:relative;padding:8px 16px max(24px,env(safe-area-inset-bottom))}
.vcard{position:relative;isolation:isolate;overflow:hidden;display:flex;align-items:center;justify-content:center;gap:8px;width:100%;height:46px;padding:0 16px;border:1px solid var(--color-contact-action);border-radius:14px;background:var(--color-contact-action);color:#fff;font-size:14px;font-weight:750;letter-spacing:-.012em;line-height:1;white-space:nowrap;text-decoration:none;transition:background-color .18s cubic-bezier(.4,0,.2,1),box-shadow .18s cubic-bezier(.4,0,.2,1),transform .18s cubic-bezier(.22,1,.36,1),border-color .18s cubic-bezier(.4,0,.2,1)}
.vcard::before{content:"";position:absolute;inset:1px 1px auto;z-index:-1;height:50%;border-radius:13px 13px 10px 10px;background:linear-gradient(180deg,rgba(255,255,255,.14),transparent);opacity:0;pointer-events:none;transition:opacity .18s ease}
.vcard:hover{background:var(--color-contact-action-hover);border-color:var(--color-contact-action-hover);transform:translateY(-2px);box-shadow:0 14px 30px color-mix(in srgb,var(--color-brand) 28%,transparent),0 4px 10px rgba(15,23,42,.08),inset 0 1px 0 rgba(255,255,255,.3)}
.vcard:hover::before{opacity:1}
.vcard:active{background:var(--color-contact-action-active);transform:none}
.vcard-icon{display:inline-flex;flex:0 0 auto;align-items:center;justify-content:center;width:28px;height:28px;border:1px solid #fff;border-radius:9px;background:#fff}
.vcard-icon span{display:block;width:20px;height:20px;background-color:var(--color-brand);-webkit-mask:center/contain no-repeat;mask:center/contain no-repeat}
.vcard-label{overflow:hidden;text-overflow:ellipsis}
.about{margin-top:16px;padding:14px 16px;border:1px solid var(--color-border);border-radius:18px;background:var(--color-surface);box-shadow:var(--shadow-sm);text-align:left}
.about p{margin:0;color:var(--color-text-secondary);font-size:13.5px;line-height:1.55;overflow-wrap:anywhere;white-space:pre-wrap}
.section{margin-top:16px;opacity:0;animation:enter .42s cubic-bezier(.22,1,.36,1) forwards}
.section:nth-of-type(1){animation-delay:.09s}.section:nth-of-type(2){animation-delay:.14s}.section:nth-of-type(3){animation-delay:.19s}.section:nth-of-type(4){animation-delay:.24s}.section:nth-of-type(5){animation-delay:.29s}
.section h2{display:flex;align-items:center;gap:12px;margin:0 0 8px;color:var(--color-text);font-size:14.5px;line-height:1.25;font-weight:700;letter-spacing:-.02em}
.section h2::after{content:"";height:1px;flex:1;background:linear-gradient(90deg,color-mix(in srgb,var(--color-primary) 24%,transparent),color-mix(in srgb,var(--color-primary) 14%,transparent),transparent)}
.grid{display:flex;flex-wrap:wrap;gap:8px}
.list{display:grid;grid-template-columns:minmax(0,1fr);gap:8px}
.link{text-decoration:none;border:1px solid var(--color-border);background:linear-gradient(145deg,var(--color-surface) 0%,var(--color-surface-muted) 100%);box-shadow:var(--shadow-sm);transition:transform .18s cubic-bezier(.22,1,.36,1),border-color .18s cubic-bezier(.4,0,.2,1),box-shadow .18s cubic-bezier(.4,0,.2,1)}
.link:hover,.link:focus-visible{transform:translateY(-2px);border-color:color-mix(in srgb,var(--color-primary) 22%,var(--color-border));box-shadow:0 14px 32px rgba(15,23,42,.09)}
.link:active{transform:translateY(0) scale(.985)}
.tile{width:var(--tile);height:var(--tile);display:flex;align-items:center;justify-content:center;border-radius:var(--tile-r);color:var(--color-primary)}
.row{min-height:58px;display:flex;align-items:center;gap:12px;padding:8px 12px;border-radius:15px;color:var(--color-text)}
.row-icon{width:var(--tile2);height:var(--tile2);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;border-radius:var(--tile2-r);background:var(--color-surface);border:1px solid var(--color-border);box-shadow:0 1px 2px rgba(15,23,42,.04),0 4px 12px rgba(15,23,42,.03)}
.mi,.tile-icon,.row-icon>span{transition:transform .18s cubic-bezier(.22,1,.36,1)}
.link:hover .tile-icon,.link:hover .row-icon>span{transform:scale(1.07)}
.mi{display:inline-block;flex-shrink:0;width:var(--tile-img);height:var(--tile-img);background-color:var(--color-brand);-webkit-mask-position:center;mask-position:center;-webkit-mask-size:contain;mask-size:contain;-webkit-mask-repeat:no-repeat;mask-repeat:no-repeat}
.mi-fallback{display:inline-flex;align-items:center;justify-content:center;font-size:var(--tile-fb);color:var(--color-brand)}
.row-text{flex:1;min-width:0}
.row-label{display:block;font-size:14px;font-weight:650;line-height:1.3;text-align:left;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--color-text-secondary)}
.row-value{display:block;margin-top:3px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--color-text-muted);font-size:12px;line-height:1.4}
.row-arrow{flex-shrink:0;color:var(--color-text-muted);font-size:13px;transition:transform .18s cubic-bezier(.22,1,.36,1),color .18s ease}
.link:hover .row-arrow{color:var(--color-primary);transform:translateX(3px)}
.footer{width:100%;min-height:48px;display:flex;align-items:center;justify-content:center;gap:10px;padding:10px 17px;margin-top:auto;background:var(--footer-bg);color:var(--color-text-secondary);font-size:12.5px;font-weight:500;line-height:1.5;border-top:1px solid var(--color-border)}
.footer a{display:flex;align-items:center}
.footer img{display:block;width:auto;height:22px}
.footer .logo-light{display:var(--logo-light)}.footer .logo-dark{display:var(--logo-dark)}
.modal{position:fixed;inset:0;z-index:1000;display:none;align-items:center;justify-content:center;padding:16px;background:var(--color-overlay);-webkit-backdrop-filter:blur(5px);backdrop-filter:blur(5px)}
.modal:target{display:flex}
.modal-backdrop{position:absolute;inset:0}
.modal-panel{position:relative;width:min(100%,430px);max-height:calc(100dvh - 32px);overflow:auto;padding:34px 22px 24px;border-radius:28px;background:var(--color-surface);box-shadow:0 28px 80px rgba(15,23,42,.24);animation:panel .32s cubic-bezier(.22,1,.36,1) both}
.modal-close{position:absolute;top:16px;right:16px;width:32px;height:32px;display:flex;align-items:center;justify-content:center;border-radius:8px;color:var(--color-text-muted);font-size:16px;text-decoration:none}
.modal-close:hover{background:var(--color-surface-muted);color:var(--color-text)}
.modal-icon{width:82px;height:82px;margin:0 auto 22px;display:flex;align-items:center;justify-content:center;border-radius:23px;background:linear-gradient(145deg,var(--color-surface) 0%,var(--color-avatar-muted) 100%);box-shadow:0 12px 28px rgba(15,23,42,.12);--tile-img:52px;--tile-fb:44px}
.modal-list{display:grid;grid-template-columns:minmax(0,1fr);gap:12px}
.modal-row{min-height:62px;display:flex;align-items:center;justify-content:center;flex-wrap:wrap;gap:4px 8px;padding:14px 18px;border:1px solid var(--color-border);border-radius:18px;background:var(--color-surface-muted);color:var(--color-text);text-align:center;text-decoration:none;box-shadow:0 8px 20px rgba(15,23,42,.055);transition:transform .18s cubic-bezier(.22,1,.36,1),border-color .18s ease,box-shadow .18s ease}
.modal-row:hover{transform:translateY(-2px);border-color:color-mix(in srgb,var(--color-primary) 20%,transparent);box-shadow:0 14px 30px rgba(15,23,42,.1)}
.modal-row span:first-child{color:var(--color-text-secondary);font-size:14px;line-height:1.4;font-weight:500}
.modal-row span:last-child{min-width:0;color:var(--color-text);font-size:15px;line-height:1.4;font-weight:500;overflow-wrap:anywhere}
#theme{position:absolute;opacity:0;pointer-events:none}
:where(a,label):focus-visible{outline:2px solid var(--color-primary);outline-offset:2px}
@keyframes enter{from{opacity:0;transform:translateY(10px)}to{opacity:1;transform:translateY(0)}}
@keyframes panel{from{opacity:0;transform:translateY(8px) scale(.99)}to{opacity:1;transform:none}}
@media (prefers-reduced-motion:reduce){*{transition:none!important}.profile,.section,.modal-panel{opacity:1;animation:none!important;transform:none}.link:hover{transform:none}}
@media (max-width:480px){.frame{box-shadow:none}}
""";

    // ------------------------------------------------------------------ render

    private sealed record Group(string Key, List<VisibleLinkRow> Rows);

    private static List<Group> Groups(IEnumerable<VisibleLinkRow> rows)
    {
        var groups = new List<Group>();
        var index = new Dictionary<string, Group>();
        foreach (var row in rows)
        {
            var key = row.Category == "other" ? row.Id : string.IsNullOrEmpty(row.Platform) ? row.Id : row.Platform!;
            if (!index.TryGetValue(key, out var group)) groups.Add(index[key] = group = new Group(key, []));
            group.Rows.Add(row);
        }
        return groups;
    }

    /// <summary><c>publicLinkHref</c>: e-mail links open Gmail compose, unsafe links are dropped.</summary>
    private static string PublicHref(string href)
    {
        if (string.IsNullOrEmpty(href) || !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return href;
        var email = href["mailto:".Length..].Split('?', 2)[0].Trim();
        return email.Length == 0 ? "" : "https://mail.google.com/mail/?view=cm&fs=1&to=" + Uri.EscapeDataString(email);
    }

    private static string LinkAttrs(string href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        var external = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return $" href=\"{H(href)}\"" + (external ? " target=\"_blank\" rel=\"noreferrer\"" : "");
    }

    public static string Render(VisibleCard card, CardHtmlImages images)
    {
        var u = card.User;
        var brand = NormalizeHex(u.Company?.BrandColor);
        var light = Palette(brand, false);
        var dark = Palette(brand, true);
        var modals = new StringBuilder();
        var modalCount = 0;

        // Opens grouped rows (same platform, several values) in a :target modal, like GroupedLinkModal.
        string Trigger(Group group, string cls, string inner, string? ariaLabel)
        {
            var row = group.Rows[0];
            var aria = ariaLabel is null ? "" : $" aria-label=\"{H(ariaLabel)}\"";
            if (group.Rows.Count == 1)
                return $"<a class=\"link {cls}\"{LinkAttrs(PublicHref(row.Href))}{aria}>{inner}</a>";

            var id = $"g{++modalCount}";
            modals.Append($"<div class=\"modal\" id=\"{id}\" role=\"dialog\" aria-modal=\"true\" aria-label=\"{H(row.Label)} seçimləri\">");
            modals.Append("<a class=\"modal-backdrop\" href=\"#_\" tabindex=\"-1\" aria-hidden=\"true\"></a><div class=\"modal-panel\">");
            modals.Append($"<a class=\"modal-close\" href=\"#_\" aria-label=\"Bağla\">{Svg("close")}</a>");
            modals.Append($"<div class=\"modal-icon\">{Icon(row, "mi")}</div><div class=\"modal-list\">");
            foreach (var r in group.Rows)
                modals.Append($"<a class=\"modal-row\"{LinkAttrs(PublicHref(r.Href))}><span>{H(r.Label)}:</span><span>{H(r.Value)}</span></a>");
            modals.Append("</div></div></div>");
            var groupAria = ariaLabel is null ? "" : $" aria-label=\"{H(row.Label)} seçimlərini aç\"";
            return $"<a class=\"link {cls}\" href=\"#{id}\"{groupAria}>{inner}</a>";
        }

        string ContactTile(Group g) => Trigger(g, "tile", $"<span class=\"tile-icon\">{Icon(g.Rows[0], "mi")}</span>", g.Rows[0].Label);
        string IconTile(Group g) => ContactTile(g);
        string LinkCard(Group g)
        {
            var row = g.Rows[0];
            var value = row.Category == "other"
                ? $"<span class=\"row-value\" title=\"{H(row.Value)}\">{H(System.Text.RegularExpressions.Regex.Replace(row.Value, "^https?://", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase))}</span>"
                : "";
            return Trigger(g, "row",
                $"<span class=\"row-icon\">{Icon(row, "mi")}</span><span class=\"row-text\"><span class=\"row-label\">{H(row.Label)}</span>{value}</span><span class=\"row-arrow\">{Svg("right")}</span>",
                null);
        }

        var sections = new (string Title, string Category, string Layout, Func<Group, string> Tile)[]
        {
            ("Əlaqələr", "contact", "grid", ContactTile),
            ("Vebsaytlar", "other", "list", LinkCard),
            ("Sosial media", "social", "grid", IconTile),
            ("Musiqi", "music", "grid", IconTile),
            ("Biznes", "business", "list", LinkCard),
        };

        var roleAndCompany = string.Join(" · ", new[] { u.JobTitle?.Trim(), u.Company?.Name?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        var background = images.Background ?? images.Logo;
        var isLogoCover = images.Background is null && images.Logo is not null;
        var about = u.AdditionalInfo?.Trim();

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html><html lang=\"az\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com\">");
        sb.Append("<meta name=\"referrer\" content=\"no-referrer\">");
        sb.Append($"<meta name=\"theme-color\" content=\"{brand}\">");
        sb.Append($"<title>{H(string.IsNullOrEmpty(roleAndCompany) ? u.FullName : $"{u.FullName} — {roleAndCompany.Replace(" · ", " — ")}")}</title>");
        // Poppins when online; system-ui keeps the layout when the file is opened offline.
        sb.Append("<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=Poppins:wght@400;500;600;700;800&amp;display=swap\">");
        sb.Append("<style>");
        sb.Append($":root{{{light}}}:root:has(#theme:checked){{{dark}}}");
        sb.Append($"@media (prefers-color-scheme:dark){{:root{{{dark}}}:root:has(#theme:checked){{{light}}}}}");
        sb.Append(Css);
        sb.Append("</style></head><body>");
        sb.Append("<input type=\"checkbox\" id=\"theme\" aria-label=\"Rejimi dəyiş\">");
        sb.Append("<main class=\"page\"><div class=\"frame\"><article class=\"card\">");

        // cover
        var coverClass = isLogoCover ? "cover cover--logo" : background is not null ? "cover" : "cover cover--accent";
        sb.Append($"<section class=\"{coverClass}\">");
        sb.Append(background is not null
            ? $"<img class=\"cover-media\" src=\"{H(background)}\" alt=\"\" aria-hidden=\"true\">"
            : "<span class=\"cover-pattern\"></span>");
        sb.Append($"<label class=\"theme-toggle\" for=\"theme\" title=\"Rejimi dəyiş\">{Svg("moon", "ico moon")}{Svg("sun", "ico sun")}</label>");
        sb.Append("</section>");

        // profile
        sb.Append("<section class=\"profile\"><div class=\"avatar-wrap\"><div class=\"avatar\">");
        sb.Append(images.Photo is not null
            ? $"<img src=\"{H(images.Photo)}\" alt=\"{H(u.FullName)}\">"
            : $"<span class=\"avatar-fallback\">{H(string.IsNullOrEmpty(u.FullName) ? "?" : u.FullName[..1].ToUpper(CultureInfo.GetCultureInfo("az")))}</span>");
        sb.Append("</div></div>");
        sb.Append($"<h1>{H(u.FullName)}</h1>");
        if (roleAndCompany.Length > 0) sb.Append($"<p class=\"role\">{H(roleAndCompany)}</p>");
        sb.Append("</section>");

        // content
        sb.Append("<section class=\"content\">");
        if (images.VcfHref is not null)
        {
            var label = string.IsNullOrWhiteSpace(card.ContactCard.ButtonLabel) ? ContactCardSettings.DefaultButtonLabel : card.ContactCard.ButtonLabel;
            var fileName = (string.IsNullOrWhiteSpace(card.ContactCard.Name) ? u.FullName : card.ContactCard.Name).Trim().Replace(' ', '_');
            var contactsIcon = Asset("icons/contacts.svg");
            sb.Append($"<a class=\"vcard\" href=\"{H(images.VcfHref)}\" download=\"{H(fileName)}.vcf\">");
            sb.Append($"<span class=\"vcard-icon\" aria-hidden=\"true\"><span style=\"-webkit-mask-image:url({contactsIcon});mask-image:url({contactsIcon})\"></span></span>");
            sb.Append($"<span class=\"vcard-label\">{H(label)}</span></a>");
        }
        if (!string.IsNullOrEmpty(about))
            sb.Append($"<section class=\"about\" aria-label=\"Profil məlumatı\"><p>{H(about)}</p></section>");

        foreach (var (title, category, layout, tile) in sections)
        {
            var groups = Groups(card.Rows.Where(r => r.Category == category && !(category == "other" && r.Id == "core:googleMapsUrl")));
            if (groups.Count == 0) continue;
            sb.Append($"<div class=\"section\"><h2>{H(title)}</h2><div class=\"{layout}\">");
            foreach (var g in groups) sb.Append(tile(g));
            sb.Append("</div></div>");
        }
        sb.Append("</section>");

        // footer
        sb.Append("<div class=\"footer\"><a href=\"https://www.setclapp.com/\" target=\"_blank\" rel=\"noopener noreferrer\" aria-label=\"SetClapp\">");
        sb.Append($"<img class=\"logo-light\" src=\"{Asset("setclapp-logo.svg")}\" alt=\"SetClapp\">");
        sb.Append($"<img class=\"logo-dark\" src=\"{Asset("setclapp-logo-white.svg")}\" alt=\"SetClapp\">");
        sb.Append($"</a><span>© 2019 - {DateTime.UtcNow.Year} SetClapp MMC</span></div>");

        sb.Append("</article></div></main>");
        sb.Append(modals);
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

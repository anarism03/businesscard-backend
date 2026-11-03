using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BusinessCard.Api.Infrastructure;

public static class SwaggerSetup
{
    public static IServiceCollection AddBusinessCardSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "BusinessCard API",
                Version = "v1",
                Description =
                    "Errors use the shape { \"code\": \"...\", \"message\": \"...\" }. Lists use { items, totalCount, page, pageSize }. " +
                    "All timestamps are UTC (ISO-8601 with Z).\n\n" +
                    "Image crop: multipart upload fields file, x, y, width, height, sourceWidth, sourceHeight. " +
                    "Coordinates are pixels of the ORIGINAL image after EXIF orientation is applied, origin at the top-left corner. " +
                    "Send all six numbers or none (no crop). The original is kept privately; re-crop with PUT /api/images/{id}/crop.",
            });
            o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
            });
            o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
            });
            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml)) o.IncludeXmlComments(xml, includeControllerXmlComments: true);
            o.SchemaFilter<ExampleSchemaFilter>();
            o.OperationFilter<ExamplesOperationFilter>();
        });
        return services;
    }
}

/// <summary>Hides write-only helper flags from the schema.</summary>
public class ExampleSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is OpenApiSchema s && s.Properties is not null)
        {
            s.Properties.Remove("brandColorSpecified");
            s.Properties.Remove("nicknameSpecified");
        }
    }
}

/// <summary>Adds request/response examples for the nickname, brand color and crop contracts.</summary>
public class ExamplesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = "/" + (context.ApiDescription.RelativePath ?? "");
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant();

        if (path.EndsWith("/nickname") && method == "PUT")
        {
            SetRequestExample(operation, """{ "nickname": "anar-ismayilzade" }""");
            SetResponseExample(operation, "200", """{ "id": "9f0b153b-f04f-4f1a-bc79-f9dcc55e5189", "nickname": "anar-ismayilzade" }""");
            SetResponseExample(operation, "400", """{ "code": "INVALID_NICKNAME", "message": "İstifadəçi adının formatı düzgün deyil." }""");
            SetResponseExample(operation, "409", """{ "code": "NICKNAME_TAKEN", "message": "Bu istifadəçi adı artıq istifadə olunur." }""");
        }

        if (path == "/api/CompanyAdmin/company" && method == "PUT")
            SetRequestExample(operation, """{ "name": "SetClapp MMC", "brandColor": "#15803d" }""");

        if (path.StartsWith("/api/cards/") && method == "GET")
        {
            // Accept UUID or nickname: plain string without uuid format.
            foreach (var p in operation.Parameters ?? [])
                if (p.Name == "id" && p is OpenApiParameter param)
                    param.Schema = new OpenApiSchema { Type = JsonSchemaType.String };
        }

        if (path.EndsWith("/crop") && method == "PUT")
        {
            SetRequestExample(operation, """{ "x": 120, "y": 80, "width": 800, "height": 800, "sourceWidth": 1080, "sourceHeight": 1440 }""");
        }

        if (operation.Responses is not null && operation.Responses.ContainsKey("200") &&
            context.ApiDescription.SupportedResponseTypes.Any(r => r.Type == typeof(Services.ImageResultDto)))
        {
            SetResponseExample(operation, "200", """
                {
                  "imageId": "5a7b8c9d-1111-4a2b-9c3d-123456789abc",
                  "originalImageId": "5a7b8c9d-1111-4a2b-9c3d-123456789abc",
                  "kind": "profilePhoto",
                  "url": "/uploads/profile-photo/2c1d7e0f9a2b4c3d8e7f6a5b4c3d2e1f.jpg",
                  "originalUrl": "/api/images/5a7b8c9d-1111-4a2b-9c3d-123456789abc/original",
                  "width": 800, "height": 800,
                  "originalWidth": 1080, "originalHeight": 1440,
                  "crop": { "x": 120, "y": 80, "width": 800, "height": 800, "sourceWidth": 1080, "sourceHeight": 1440 },
                  "userId": "9f0b153b-f04f-4f1a-bc79-f9dcc55e5189",
                  "companyId": "0c9b2b0e-2222-4d5e-8f9a-abcdef012345"
                }
                """);
        }
    }

    private static void SetRequestExample(OpenApiOperation op, string json)
    {
        if (op.RequestBody?.Content is null) return;
        foreach (var media in op.RequestBody.Content.Values)
            if (media is OpenApiMediaType m) m.Example = System.Text.Json.Nodes.JsonNode.Parse(json);
    }

    private static void SetResponseExample(OpenApiOperation op, string status, string json)
    {
        if (op.Responses is null || !op.Responses.TryGetValue(status, out var response) || response.Content is null) return;
        foreach (var media in response.Content.Values)
            if (media is OpenApiMediaType m) m.Example = System.Text.Json.Nodes.JsonNode.Parse(json);
    }
}

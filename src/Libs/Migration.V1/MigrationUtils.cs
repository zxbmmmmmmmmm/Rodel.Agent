﻿using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RodelAgent.Interfaces;

namespace Migration.V1;

public sealed class MigrationUtils
{
    private readonly string _workDir;
    private readonly string _secretDbPath;
    private readonly string _chatDbPath;
    private readonly string _drawDbPath;
    private readonly IStorageService _storageService;

    private RodelChat.Models.Constants.ProviderType _preferChatProvider = RodelChat.Models.Constants.ProviderType.OpenAI;


    public MigrationUtils(string workDir, IStorageService storageService)
    {
        _workDir = workDir;
        _storageService = storageService;
        _secretDbPath = Path.Combine(workDir, "_secret_.db");
        _chatDbPath = Path.Combine(workDir, "chat.db");
        _drawDbPath = Path.Combine(workDir, "draw.db");
    }

    public async Task MigrateAsync()
    {
        var v2TempDir = Path.Combine(_workDir, "v2_temp");

        if (!Directory.Exists(v2TempDir))
        {
            await PackDataAsync();
            _storageService.SetWorkingDirectory(Path.Combine(_workDir, "v2_temp"));
            await MigrateSecretDataAsync();
            await MigrateChatDataAsync();
            await MigrateDrawDataAsync();
        }
        else
        {
            await CleanFolderAsync();
            await MoveV2DataAsync();
        }

        _storageService.SetWorkingDirectory(_workDir);
    }

    /// <summary>
    /// 把文件夹内的所有数据打包成zip.
    /// </summary>
    /// <returns></returns>
    private async Task PackDataAsync()
    {
        await Task.Run(() =>
        {
            var zipPath = Path.Combine(Path.GetDirectoryName(_workDir)!, "v1_backup.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(_workDir, zipPath, CompressionLevel.Fastest, false);
            File.Move(zipPath, Path.Combine(_workDir, "v1_backup.zip"));

            var v2TempDir = Path.Combine(_workDir, "v2_temp");
            if (!Directory.Exists(v2TempDir))
            {
                Directory.CreateDirectory(v2TempDir);
            }
        });
    }

    private async Task CleanFolderAsync()
    {
        await Task.Run(() =>
        {
            // Remove all files and folders in workDir exclude "v1_backup.zip" and "v2_temp" folder.
            foreach (var file in Directory.GetFiles(_workDir))
            {
                if (Path.GetFileName(file) != "v1_backup.zip")
                {
                    File.Delete(file);
                }
            }

            foreach (var dir in Directory.GetDirectories(_workDir))
            {
                if (Path.GetFileName(dir) != "v2_temp")
                {
                    Directory.Delete(dir, true);
                }
            }
        });
    }

    private async Task MoveV2DataAsync()
    {
        // move files and folder from v2_temp folder to workDir folder
        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(Path.Combine(_workDir, "v2_temp")))
            {
                File.Move(file, Path.Combine(_workDir, Path.GetFileName(file)));
            }

            foreach (var dir in Directory.GetDirectories(Path.Combine(_workDir, "v2_temp")))
            {
                Directory.Move(dir, Path.Combine(_workDir, Path.GetFileName(dir)));
            }

            Directory.Delete(Path.Combine(_workDir, "v2_temp"), true);
        });
    }

    private async Task MigrateSecretDataAsync()
    {
        using var secretDbContextV1 = new Context.SecretDbContext(_secretDbPath);
        var metadata = secretDbContextV1.Metadata.ToList();

        // Azure Open AI Chat
        if (metadata.Any(p => p.Id == "AzureOpenAIAccessKey" && !string.IsNullOrEmpty(p.Value)))
        {
            var aoaiConfig = new RodelChat.Models.Client.AzureOpenAIClientConfig
            {
                Key = metadata.First(p => p.Id == "AzureOpenAIAccessKey").Value,
                Endpoint = metadata.First(p => p.Id == "AzureOpenAIEndpoint").Value,
                Version = RodelAgent.Models.Constants.AzureOpenAIVersion.V2024_02_01,
            };

            var json = JsonSerializer.Serialize(aoaiConfig);

            var defaultModel = metadata.FirstOrDefault(p => p.Id == "DefaultAzureOpenAIChatModelName")?.Value;
            if (!string.IsNullOrEmpty(defaultModel))
            {
                aoaiConfig.CustomModels = new List<RodelChat.Models.Client.ChatModel>
                {
                    new RodelChat.Models.Client.ChatModel
                    {
                        Id = defaultModel,
                        DisplayName = defaultModel,
                        IsCustomModel = true,
                    }
                };
            }

            _preferChatProvider = RodelChat.Models.Constants.ProviderType.AzureOpenAI;
            await _storageService.SetChatConfigAsync(RodelChat.Models.Constants.ProviderType.AzureOpenAI, aoaiConfig);

            var drawAoaiConfig = JsonSerializer.Deserialize<RodelDraw.Models.Client.AzureOpenAIClientConfig>(json);
            var audioAoaiConfig = JsonSerializer.Deserialize<RodelAudio.Models.Client.AzureOpenAIClientConfig>(json);
            await _storageService.SetDrawConfigAsync(RodelDraw.Models.Constants.ProviderType.AzureOpenAI, drawAoaiConfig!);
            await _storageService.SetAudioConfigAsync(RodelAudio.Models.Constants.ProviderType.AzureOpenAI, audioAoaiConfig!);
        }

        // Open AI Chat
        if (metadata.Any(p => p.Id == "OpenAIAccessKey" && !string.IsNullOrEmpty(p.Value)))
        {
            var oaiConfig = new RodelChat.Models.Client.OpenAIClientConfig
            {
                Key = metadata.First(p => p.Id == "OpenAIAccessKey").Value,
                Endpoint = metadata.First(p => p.Id == "OpenAICustomEndpoint").Value,
                OrganizationId = metadata.First(p => p.Id == "OpenAIOrganization").Value,
            };

            var json = JsonSerializer.Serialize(oaiConfig);
            var drawOaiConfig = JsonSerializer.Deserialize<RodelDraw.Models.Client.OpenAIClientConfig>(json);
            var audioOaiConfig = JsonSerializer.Deserialize<RodelAudio.Models.Client.OpenAIClientConfig>(json);

            await _storageService.SetChatConfigAsync(RodelChat.Models.Constants.ProviderType.OpenAI, oaiConfig);
            await _storageService.SetDrawConfigAsync(RodelDraw.Models.Constants.ProviderType.OpenAI, drawOaiConfig!);
            await _storageService.SetAudioConfigAsync(RodelAudio.Models.Constants.ProviderType.OpenAI, audioOaiConfig!);
        }

        // Azure Speech
        if (metadata.Any(p => p.Id == "AzureSpeechKey" && !string.IsNullOrEmpty(p.Value)))
        {
            var azureSpeechConfig = new RodelAudio.Models.Client.AzureSpeechClientConfig
            {
                Key = metadata.First(p => p.Id == "AzureSpeechKey").Value,
                Region = metadata.First(p => p.Id == "AzureSpeechRegion").Value,
            };

            await _storageService.SetAudioConfigAsync(RodelAudio.Models.Constants.ProviderType.AzureSpeech, azureSpeechConfig);
        }

        // Azure Translator
        if (metadata.Any(p => p.Id == "AzureTranslateKey" && !string.IsNullOrEmpty(p.Value)))
        {
            var azureTranslatorConfig = new RodelTranslate.Models.Client.AzureClientConfig
            {
                Key = metadata.First(p => p.Id == "AzureTranslateKey").Value,
                Region = metadata.First(p => p.Id == "AzureTranslateRegion").Value,
            };

            await _storageService.SetTranslateConfigAsync(RodelTranslate.Models.Constants.ProviderType.Azure, azureTranslatorConfig);
        }

        // Baidu Translator
        if (metadata.Any(p => p.Id == "BaiduTranslateAppKey" && !string.IsNullOrEmpty(p.Value)))
        {
            var baiduTranslatorConfig = new RodelTranslate.Models.Client.BaiduClientConfig
            {
                AppId = metadata.First(p => p.Id == "BaiduTranslateAppId").Value,
                Key = metadata.First(p => p.Id == "BaiduTranslateAppKey").Value,
            };

            await _storageService.SetTranslateConfigAsync(RodelTranslate.Models.Constants.ProviderType.Baidu, baiduTranslatorConfig);
        }
    }

    private async Task MigrateChatDataAsync()
    {
        using var chatDbContextV1 = new Context.ChatDbContext(_chatDbPath);
        var chatSessions = chatDbContextV1.Sessions.Include(p => p.Messages).Include(p => p.Options).ToList();
        foreach (var session in chatSessions)
        {
            var instruction = string.Empty;
            if (session.Assistants?.Count != 0)
            {
                var assistant = chatDbContextV1.Assistants.FirstOrDefault(p => p.Id == session.Assistants!.First());
                instruction = assistant?.Instruction ?? string.Empty;
            }

            var messages = new List<RodelChat.Models.Client.ChatMessage>();
            if (session.Messages != null)
            {
                foreach (var message in session.Messages)
                {
                    if (message.Role == Models.ChatMessageRole.System)
                    {
                        continue;
                    }

                    messages.Add(new RodelChat.Models.Client.ChatMessage
                    {
                        ClientMessageType = RodelChat.Models.Constants.ClientMessageType.Normal,
                        Content = new List<RodelChat.Models.Client.ChatMessageContent>
                        {
                            new RodelChat.Models.Client.ChatMessageContent
                            {
                                Type = RodelChat.Models.Constants.ChatContentType.Text,
                                Text = message.Content ?? string.Empty,
                            }
                        },
                        Role = message.Role == Models.ChatMessageRole.User ? RodelChat.Models.Constants.MessageRole.User : RodelChat.Models.Constants.MessageRole.Assistant,
                        Time = message.Time,
                    });
                }
            }

            var newSession = new RodelChat.Models.Client.ChatSession
            {
                Id = session.Id!,
                Title = session.Title ?? string.Empty,
                SystemInstruction = instruction,
                UseStreamOutput = true,
                Messages = messages,
                Provider = _preferChatProvider,
            };

            await _storageService.AddOrUpdateChatSessionAsync(newSession);
        }
    }

    private async Task MigrateDrawDataAsync()
    {
        using var drawDbContextV1 = new Context.DrawDbContext(_drawDbPath);
        var drawSessions = drawDbContextV1.Images.ToList();
        foreach (var session in drawSessions)
        {
            var newSession = new RodelDraw.Models.Client.DrawSession
            {
                Id = session.Id!,
                Request = new RodelDraw.Models.Client.DrawRequest
                {
                    Size = $"{session.Width}x{session.Height}",
                    Prompt = session.Prompt ?? string.Empty,
                },
                Provider = RodelDraw.Models.Constants.ProviderType.OpenAI,
                Model = "dall-e-2",
                Time = session.Time,
            };

            var imageFile = Path.Combine(_workDir, session.Link!);
            var newImageFile = Path.Combine(_workDir, "v2_temp", "Draw", session.Id! + ".png");
            if (!Directory.Exists(Path.GetDirectoryName(newImageFile)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newImageFile)!);
            }

            File.Move(imageFile, newImageFile, true);
            await _storageService.AddOrUpdateDrawSessionAsync(newSession, default);
        }
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using ChatOps.Services.FileService;
using ChatOps.Services.SystemService;
using ChatOps.Services.RedisService;

namespace ChatOps.Services.DockerService.Create
{
    public static class DockerCreateContainer
    {
        // Hàm cấu trúc hóa dữ liệu đường dẫn động để tái sử dụng, tránh trùng lặp mã nguồn
        private class RuntimePathConfig
        {
            public string RuntimeDir { get; set; } = string.Empty;
            public string LbSubDir { get; set; } = string.Empty;
            public string LogDir { get; set; } = string.Empty;
            public string TemplateLbFile { get; set; } = string.Empty;
            public string DestLbFile { get; set; } = string.Empty;
        }

        private static RuntimePathConfig GetAppRuntimePaths(string appName)
        {
            string userHomePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string runtimeDir = Path.Combine(userHomePath, "ChatOps", "docker", "Apps", appName);
            
            return new RuntimePathConfig
            {
                RuntimeDir = runtimeDir,
                LbSubDir = Path.Combine(runtimeDir, "lb"),
                LogDir = Path.Combine(runtimeDir, "logs"),
                TemplateLbFile = Path.Combine(userHomePath, "ChatOps", "services", "LB", "docker-compose-lb.yml"),
                DestLbFile = Path.Combine(runtimeDir, "docker-compose-lb.yml")
            };
        }

        // =================================================================================
        // 1. DEPLOY SINGLE: Chạy container dịch vụ đơn lẻ (Nginx, MySQL, PgAdmin, v.v.)
        // =================================================================================
        public static async Task<string> DeploySingleContainerAsync(
            string containerName,
            string dockerImage,
            string networkName = "",
            string portMapping = "",
            string envArgs = "",
            string? volumeArgs = "",
            string labelArgs = "")
        {
            string formattedNetwork = string.IsNullOrWhiteSpace(networkName) ? "" : $"--network {networkName.Trim()} ";
            string formattedLabels = string.IsNullOrWhiteSpace(labelArgs) ? "" : labelArgs.Trim() + " ";
            string formattedEnv = string.IsNullOrWhiteSpace(envArgs) ? "" : envArgs.Trim() + " ";
            string formattedVolumes = string.IsNullOrWhiteSpace(volumeArgs) ? "" : volumeArgs.Trim() + " ";
            string formattedPorts = string.IsNullOrWhiteSpace(portMapping) ? "" : portMapping.Trim() + " ";

            string command = $"docker run -d " +
                             $"--name {containerName.Trim()} " +
                             formattedNetwork +
                             formattedLabels +
                             formattedEnv +
                             formattedVolumes +
                             formattedPorts +
                             $"{dockerImage.Trim()}";

            return await Task.Run(async () => await SystemCommandService.RunAsync(command));
        }

        // =================================================================================
        // 2. DEPLOY GIT (Staging/Develop): Lấy file từ thư mục gốc của App, tự build + dựng LB
        // =================================================================================
        public static async Task<string> DeployFromGitSource(
            string projectPath,
            string appName,
            string owner,
            List<int> extPort,
            string domain,
            string IMGDB,
            string IMGBACKEND,
            string IMGWEB,
            string IMGLB,
            bool isDebug,         
            string connectionId)  
        {
            var paths = GetAppRuntimePaths(appName);
            string templateAppFile = Path.Combine(projectPath.Trim(), "docker-git.yml");
            string destAppFile = Path.Combine(paths.RuntimeDir, "docker-git.yml");

            try
            {
                // =====================================================
                // PREPARE FILES
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, $"📂 [Docker Engine] Đang khởi tạo và chuẩn bị cấu trúc thư mục runtime tại `{paths.RuntimeDir}`...");

                string result = await LoadBalancerFileService.PrepareRuntimeDirectory(
                    paths.RuntimeDir,
                    paths.LbSubDir,
                    paths.LogDir,
                    projectPath,
                    templateAppFile,
                    destAppFile,
                    paths.TemplateLbFile,
                    paths.DestLbFile);

                if (result != "Success")
                    return result;

                // =====================================================
                // VALIDATE DOCKER COMPOSE
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "🔍 [Docker Engine] Đang tiến hành kiểm tra tính hợp lệ (Validate) của tệp cấu hình `docker-git.yml`...");

                var validation = await DockerComposeValidator.ValidateForDeployAsync(destAppFile);

                if (!validation.IsValid)
                {
                    return $@"❌ File docker-git.yml không hợp lệ

                    {validation.Message}";
                }

                // =====================================================
                // FIND FRONTEND / BACKEND FOR NGINX
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "🧩 [Docker Engine] Đang phân tích kiến trúc dịch vụ (Frontend/Backend Target Port)...");

                var nginxTarget = await DockerComposeAnalyzer.GetNginxTargetsAsync(destAppFile);

                if (string.IsNullOrWhiteSpace(nginxTarget.Backend) || !Directory.Exists(Path.Combine(projectPath, nginxTarget.Backend)))
                {
                    return $"❌ Kho mã nguồn thiếu thư mục chứa code backend cấu hình theo service: '{nginxTarget.Backend ?? "backend"}'";
                }

                if (string.IsNullOrWhiteSpace(nginxTarget.Frontend) || !Directory.Exists(Path.Combine(projectPath, nginxTarget.Frontend)))
                {
                    return $"❌ Kho mã nguồn thiếu thư mục chứa code frontend cấu hình theo service: '{nginxTarget.Frontend ?? "frontend"}'";
                }

                // =====================================================
                // GENERATE NGINX CONFIG
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, $"⚙️ [Docker Engine] Sinh file cấu hình định tuyến Nginx nội bộ ({nginxTarget.Frontend}:{nginxTarget.FrontendPort} -> {nginxTarget.Backend}:{nginxTarget.BackendPort})...");

                await LoadBalancerFileService.GenerateNginxConfigAsync(
                    paths.LbSubDir,
                    nginxTarget.Frontend,
                    nginxTarget.FrontendPort,
                    nginxTarget.Backend,
                    nginxTarget.BackendPort);

                // =====================================================
                // GENERATE LB DOCKERFILE
                // =====================================================
                await LoadBalancerFileService.GenerateLbDockerfileAsync(paths.LbSubDir);

                // =====================================================
                // GENERATE ENV
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "📝 [Docker Engine] Đang kết xuất và đồng bộ tệp biến môi trường `.env`...");

                await LoadBalancerFileService.WriteEnvFileAsync(
                    paths.RuntimeDir,
                    appName,
                    IMGDB,
                    IMGBACKEND,
                    IMGWEB,
                    IMGLB,
                    owner,
                    domain,
                    extPort);

                // =====================================================
                // PERMISSION
                // =====================================================
                await SystemCommandService.RunAsync($"chmod -R 777 {paths.RuntimeDir}");

                string cdCmd = $"cd {paths.RuntimeDir}";

                // =====================================================
                // DEPLOY APP
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "🚀 [Docker Engine] Đang thực thi lệnh `docker compose up --build` cho các dịch vụ Core App (DB, Backend, Frontend). Quá trình này có thể mất vài phút...");

                string appResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-git.yml up -d --build 2>&1");

                // =====================================================
                // DEPLOY LOAD BALANCER
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "⚖️ [Docker Engine] Dịch vụ lõi khởi tạo hoàn tất. Đang triển khai cụm Load Balancer (Nginx-LB) biên dịch riêng...");

                string lbResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-compose-lb.yml up -d --build --force-recreate 2>&1");

                // =====================================================
                // CHECK LB STATUS
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "📊 [Docker Engine] Thu thập dữ liệu trạng thái tài nguyên mạng và cổng Cluster...");

                string psResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-compose-lb.yml ps lb");

                return $@"=== DEPLOY APP RESULT ===

                {appResult}

                === DEPLOY LB RESULT ===

                {lbResult}

                === NGINX ROUTING ===

                Frontend : {nginxTarget.Frontend}:{nginxTarget.FrontendPort}

                Backend  : {nginxTarget.Backend}:{nginxTarget.BackendPort}

                === CLUSTER PORTS ===

                {psResult}";
            }
            catch (Exception ex)
            {
                return $"❌ Deploy thất bại: {ex.Message}";
            }
        }

        // =================================================================================
        // 3. DEPLOY REGISTRY (Production): Pull ảnh hoàn chỉnh từ Registry về chạy luôn
        // =================================================================================
        public static async Task<string> DeployFromRegistry(
            string projectPath,
            string appName,
            string owner,
            List<int> extPort,
            string domain,
            string IMGDB,
            string IMGBACKEND,
            string IMGWEB,
            string IMGLB,
            bool isDebug,         
            string connectionId)  
        {
            var paths = GetAppRuntimePaths(appName);
            string templateAppFile = Path.Combine(projectPath.Trim(), "docker-registry.yml");
            string destAppFile = Path.Combine(paths.RuntimeDir, "docker-registry.yml");

            try
            {
                // =====================================================
                // PREPARE FILES
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, $"📂 [Docker Registry] Chuẩn bị không gian lưu trữ cho Production tại `{paths.RuntimeDir}`...");

                string result = await LoadBalancerFileService.PrepareRuntimeDirectory(
                    paths.RuntimeDir,
                    paths.LbSubDir,
                    paths.LogDir,
                    projectPath,
                    templateAppFile,
                    destAppFile,
                    paths.TemplateLbFile,
                    paths.DestLbFile);

                if (result != "Success")
                    return result;

                // =====================================================
                // VALIDATE DOCKER COMPOSE
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "🔍 [Docker Registry] Kiểm tra cấu trúc file cấu hình Production `docker-registry.yml`...");

                var validation = await DockerComposeValidator.ValidateForDeployAsync(destAppFile);

                if (!validation.IsValid)
                {
                    return $@"❌ File docker-registry.yml không hợp lệ

                    {validation.Message}";
                }

                // =====================================================
                // FIND FRONTEND / BACKEND FOR NGINX
                // =====================================================
                var nginxTarget = await DockerComposeAnalyzer.GetNginxTargetsAsync(destAppFile);

                if (string.IsNullOrWhiteSpace(nginxTarget.Backend))
                {
                    return "❌ Cấu hình thiếu backend.";
                }

                if (string.IsNullOrWhiteSpace(nginxTarget.Frontend))
                {
                    return "❌ Cấu hình thiếu frontend.";
                }

                // =====================================================
                // GENERATE NGINX CONFIG
                // =====================================================
                await LoadBalancerFileService.GenerateNginxConfigAsync(
                    paths.LbSubDir,
                    nginxTarget.Frontend,
                    nginxTarget.FrontendPort,
                    nginxTarget.Backend,
                    nginxTarget.BackendPort);

                // =====================================================
                // GENERATE LB DOCKERFILE
                // =====================================================
                await LoadBalancerFileService.GenerateLbDockerfileAsync(paths.LbSubDir);

                // =====================================================
                // GENERATE ENV
                // =====================================================
                await LoadBalancerFileService.WriteEnvFileAsync(
                    paths.RuntimeDir,
                    appName,
                    IMGDB,
                    IMGBACKEND,
                    IMGWEB,
                    IMGLB,
                    owner,
                    domain,
                    extPort);

                // =====================================================
                // PERMISSION
                // =====================================================
                await SystemCommandService.RunAsync($"chmod -R 777 {paths.RuntimeDir}");

                string cdCmd = $"cd {paths.RuntimeDir}";

                // =====================================================
                // DEPLOY APP
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "🚚 [Docker Registry] Đang kéo (Pull) các Image dựng sẵn từ Registry và triển khai cụm Container...");

                string appResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-registry.yml up -d --build 2>&1");

                // =====================================================
                // DEPLOY LOAD BALANCER
                // =====================================================
                await RedisChannelService.SendMessageToClientAsync(isDebug, connectionId, "⚖️ [Docker Registry] Đang thiết lập cơ chế cân bằng tải biên dịch Production...");

                string lbResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-compose-lb.yml up -d --build --force-recreate 2>&1");

                // =====================================================
                // CHECK LB STATUS
                // =====================================================
                string psResult = await SystemCommandService.RunAsync($"{cdCmd} && docker compose -f docker-compose-lb.yml ps lb");

                return $@"=== DEPLOY APP RESULT ===

                {appResult}

                === DEPLOY LB RESULT ===

                {lbResult}

                === NGINX ROUTING ===

                Frontend : {nginxTarget.Frontend}:{nginxTarget.FrontendPort}

                Backend  : {nginxTarget.Backend}:{nginxTarget.BackendPort}

                === CLUSTER PORTS ===

                {psResult}";
            }
            catch (Exception ex)
            {
                return $"❌ Deploy thất bại: {ex.Message}";
            }
        }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    public class CredentialService
    {
        private readonly string _credentialsFilePath;
        private readonly byte[] _entropy;

        public CredentialService()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appDataPath, "JiraWorklogViewer");
            
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }

            _credentialsFilePath = Path.Combine(appFolder, "credentials.dat");
            
            // Use machine-specific entropy for additional security
            _entropy = Encoding.UTF8.GetBytes(Environment.MachineName + "JiraWorklogViewer");
        }

        public void SaveCredentials(JiraCredentials credentials)
        {
            var json = JsonConvert.SerializeObject(credentials);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            
            // Use DPAPI for encryption (Windows Data Protection API)
            var encryptedBytes = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser);
            
            File.WriteAllBytes(_credentialsFilePath, encryptedBytes);
        }

        public JiraCredentials LoadCredentials()
        {
            if (!File.Exists(_credentialsFilePath))
            {
                return null;
            }

            try
            {
                var encryptedBytes = File.ReadAllBytes(_credentialsFilePath);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, _entropy, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plainBytes);
                
                return JsonConvert.DeserializeObject<JiraCredentials>(json);
            }
            catch
            {
                // If decryption fails, return null
                return null;
            }
        }

        public void ClearCredentials()
        {
            if (File.Exists(_credentialsFilePath))
            {
                File.Delete(_credentialsFilePath);
            }
        }

        public bool HasStoredCredentials()
        {
            return File.Exists(_credentialsFilePath);
        }
    }
}

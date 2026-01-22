using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace AutomationTools
{
    public class ADORestAPI
    {
        private readonly HttpClient _httpClient;
        // NOTE: PAT must NOT be hardcoded. Configure via environment variable: ADO_PAT
        private readonly string _personalAccessToken;

        // Base org+project URL (no trailing slash), e.g. https://dev.azure.com/InfraEdgeSys/InfraEdge
        private readonly string _baseProjectUrl = "https://dev.azure.com/InfraEdgeSys/InfraEdge";
        private readonly string _testPlanBaseUrl;

        public ADORestAPI()
        {
            _httpClient = new HttpClient();
            _personalAccessToken = Environment.GetEnvironmentVariable("ADO_PAT")
                ?? throw new InvalidOperationException("Missing env var ADO_PAT (Azure DevOps Personal Access Token).");
            _testPlanBaseUrl = $"{_baseProjectUrl}/_apis/testplan/plans";
        }

        public string GetTestPlans()
        {
            string url = $"{_testPlanBaseUrl}?api-version=7.1-preview.1";
            
            // Encode PAT as Base64 for authentication
            var authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{_personalAccessToken}"));

            // Set Authorization header
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                // Synchronous GET request
                HttpResponseMessage response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode(); // Throws exception if response is not 2xx

                // Read response synchronously
                string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return jsonResponse;
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Request error: {e.Message}");
                return $"Error: {e.Message}";
            }
        }

        public string GetTestSuites(int testPlanID)
        {
            string url = $"{_testPlanBaseUrl}/{testPlanID}/suites?api-version=7.1-preview.1";
           
            // Encode PAT as Base64 for authentication
            var authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{_personalAccessToken}"));

            // Set Authorization header
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                // Synchronous GET request
                HttpResponseMessage response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode(); // Throws exception if response is not 2xx

                // Read response synchronously
                string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return jsonResponse;
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Request error: {e.Message}");
                return $"Error: {e.Message}";
            }
        }

        public string SetTestStatus(int testPlanID)
        {
            // NOTE: This method isn't used by the Allure->TestCase sync, but should compile and behave deterministically.
            string url = $"{_baseProjectUrl}/_apis/test/runs?api-version=7.1-preview.1";
           
            // Encode PAT as Base64 for authentication
            var authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{_personalAccessToken}"));

            // Set Authorization header
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payloadJson = new
            {
                name = $"Automated UI Run {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                isAutomated = true
            };
                        
            //var payloadJson = "{\"name\":\"test\"}";
            var content = new StringContent(JsonSerializer.Serialize(payloadJson), Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response = _httpClient.PostAsync(url, content).GetAwaiter().GetResult(); 
                response.EnsureSuccessStatusCode(); // Throws exception if response is not 2xx

                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();             

                using JsonDocument doc = JsonDocument.Parse(json);
                int runId = doc.RootElement.GetProperty("id").GetInt32();

                var resultsBody = new[]
                {
                    new {
                        testCase = new { id = 3772 },
                        outcome  = "Passed",
                        state    = "Completed",
                        automatedTestName = "001 Creating \"request\" with one subject \"ForApproval\" and comment, mail sending, History page validation, Approval panel - \"Approve\", History page validation",
                        durationInMs = 5321
                    }
                };

                var resultsUrl = $"{_baseProjectUrl}/_apis/test/runs/{runId}/results?api-version=7.1-preview.1";
                var resultsContent = new StringContent(JsonSerializer.Serialize(resultsBody), Encoding.UTF8, "application/json");
                HttpResponseMessage resultsResponse = _httpClient.PostAsync(resultsUrl, resultsContent).GetAwaiter().GetResult();
                resultsResponse.EnsureSuccessStatusCode();

                return resultsResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();


                //// Read response synchronously
                //string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                //return jsonResponse;
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Request error: {e.Message}");
                return $"Error: {e.Message}";
            }
        }
    }
}

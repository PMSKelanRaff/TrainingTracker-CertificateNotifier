using System;
using System.Data;
using System.Net;
using System.Net.Mail;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CertificateNotifier
{
    class Program
    {
        static void Main(string[] args)
        {
            // Load configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            string connectionString = config["Database:ConnectionString"];
            string smtpHost = config["Smtp:Host"];
            int smtpPort = int.Parse(config["Smtp:Port"]);
            string smtpUser = config["Smtp:User"];
            string managerEmail = config["Smtp:ManagerEmail"];
            string smtpPass = config["Smtp:Password"];

            if (string.IsNullOrEmpty(smtpPass))
            {
                Console.WriteLine("Missing Gmail App Password. Please set GMAIL_APP_PASSWORD env variable.");
                return;
            }

            Console.WriteLine("Configuration loaded. Starting certificate notifier...");

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT tc.CertificateID, tc.CertificateName, tc.ExpiryDate, u.Username AS Email
                        FROM TrainingCertificates tc
                        JOIN Users u ON u.EmployeeID = tc.EmployeeID
                        WHERE date(tc.ExpiryDate, '-2 months') <= date('now')
                          AND tc.LastNotifiedDate IS NULL;
                    ";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long certId = reader.GetInt64(0);
                            string certName = reader.GetString(1);
                            DateTime expiryDate = DateTime.Parse(reader.GetString(2));
                            string employeeEmail = reader.GetString(3);

                          
                            // Only update DB if email succeeded
                            bool emailSent = SendEmail(employeeEmail, managerEmail, certName, expiryDate, smtpHost, smtpPort, smtpUser, smtpPass);

                            if (emailSent)
                            {
                                using (var updateCmd = conn.CreateCommand())
                                {
                                    updateCmd.CommandText = @"
                                        UPDATE TrainingCertificates
                                        SET LastNotifiedDate = date('now')
                                        WHERE CertificateID = @CertificateID;
                                    ";
                                    updateCmd.Parameters.AddWithValue("@CertificateID", certId);
                                    updateCmd.ExecuteNonQuery();
                                }
                                Log($" Notification sent for {employeeEmail} - {certName}");
                            }
                            else
                            {
                                Log($" Failed to send notification for {employeeEmail} - {certName}");
                            }
                        }
                    }
                }
            }

            Console.WriteLine(" Done.");
        }

        static bool SendEmail(string toEmail, string managerEmail, string certName, DateTime expiry,
                       string smtpHost, int smtpPort, string smtpUser, string smtpPass)
        {
            try
            {
                using (var client = new SmtpClient(smtpHost, smtpPort))
                {
                    client.EnableSsl = true;
                    client.Credentials = new NetworkCredential(smtpUser, smtpPass);

                    var mail = new MailMessage
                    {
                        From = new MailAddress(smtpUser, "Training Tracker"),
                        Subject = $"Certificate Expiry Reminder: {certName}",
                        Body = $"Your certificate \"{certName}\" is expiring on {expiry:yyyy-MM-dd}. Please schedule training.",
                        IsBodyHtml = false
                    };

                    mail.To.Add(toEmail);

                    if (!string.Equals(managerEmail, toEmail, StringComparison.OrdinalIgnoreCase))
                        mail.CC.Add(managerEmail);

                    mail.ReplyToList.Add(new MailAddress(smtpUser));

                    // Optional: HTML alternate view
                    string htmlBody = $"<p>Your certificate <strong>{certName}</strong> is expiring on <strong>{expiry:yyyy-MM-dd}</strong>. Please schedule training.</p>";
                    var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html");
                    mail.AlternateViews.Add(htmlView);

                    client.Send(mail); // If no exception, email sent
                }

                return true; // success
            }
            catch (SmtpException ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - SMTP Error sending to {toEmail}: {ex.StatusCode} - {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - General error sending to {toEmail}: {ex.Message}");
                return false;
            }
        }




        static void Log(string message)
        {
            string logFolder = AppContext.BaseDirectory + "Logs";
            if (!System.IO.Directory.Exists(logFolder))
                System.IO.Directory.CreateDirectory(logFolder);

            string logFile = System.IO.Path.Combine(logFolder, $"NotificationLog_{DateTime.Now:yyyyMMdd}.txt");
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
            Console.WriteLine(logEntry);
            System.IO.File.AppendAllText(logFile, logEntry + Environment.NewLine);
        }
    }
}


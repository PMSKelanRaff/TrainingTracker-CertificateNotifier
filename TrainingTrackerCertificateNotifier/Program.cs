using System;
using System.Data;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using Npgsql;

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

            // Build the connection string from the individual JSON fields
            string dbHost = config["Database:Host"];
            string dbPort = config["Database:Port"];
            string dbName = config["Database:Name"];
            string dbUser = config["Database:User"];
            string dbPass = config["Database:Pass"];
            string dbSsl = config["Database:SslMode"];

            string connectionString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass};Ssl Mode={dbSsl};";

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

            using (var conn = new NpgsqlConnection(connectionString))
            {
                conn.Open();

                // 1. Read certificates into memory first
                var pendingCerts = new List<(long CertId, string CertName, DateTime ExpiryDate, string EmployeeEmail)>();

                using (var cmd = conn.CreateCommand())
                {
                    // ---> REPLACE THIS SQL QUERY <---
                    cmd.CommandText = @"
                        SELECT tc.CertificateID, tc.CertificateName, tc.ExpiryDate, u.Email
                        FROM TrainingCertificates tc
                        JOIN Users u ON u.EmployeeID = tc.EmployeeID
                        WHERE tc.ExpiryDate IS NOT NULL
                          AND tc.ExpiryDate != '' 
                          AND tc.ExpiryDate::date <= CURRENT_DATE + INTERVAL '2 months'
                          AND tc.LastNotifiedDate IS NULL;
                    ";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            pendingCerts.Add((
                                reader.GetInt64(0),
                                reader.GetString(1),
                                DateTime.Parse(reader.GetString(2)), // Back to parsing strings
                                reader.GetString(3)
                            ));
                        }
                    } // Reader is safely closed here
                }

                // 2. Loop through the list to send emails and update the database
                foreach (var cert in pendingCerts)
                {
                    bool emailSent = SendEmail(cert.EmployeeEmail, managerEmail, cert.CertName, cert.ExpiryDate, smtpHost, smtpPort, smtpUser, smtpPass);

                    if (emailSent)
                    {
                        using (var updateCmd = conn.CreateCommand())
                        {
                            updateCmd.CommandText = @"
                                UPDATE TrainingCertificates
                                SET LastNotifiedDate = CURRENT_DATE::text
                                WHERE CertificateID = @CertificateID;
                            ";
                            updateCmd.Parameters.AddWithValue("@CertificateID", cert.CertId);
                            updateCmd.ExecuteNonQuery();
                        }
                        Log($" Notification sent for {cert.EmployeeEmail} - {cert.CertName}");
                    }
                    else
                    {
                        Log($" Failed to send notification for {cert.EmployeeEmail} - {cert.CertName}");
                    }
                }

                // 3. Check upcoming sessions
                NotifyUpcomingTrainingSessions(conn, smtpHost, smtpPort, smtpUser, smtpPass);

            }

            Console.WriteLine(" Done.");
        }

        static void NotifyUpcomingTrainingSessions(NpgsqlConnection conn, string smtpHost, int smtpPort, string smtpUser, string smtpPass)
        {
            Console.WriteLine("Checking for upcoming training sessions...");

            // Create a list to hold the sessions in memory
            var pendingSessions = new List<(long SessionId, string SessionName, DateTime PlannedDate)>();

            // 1. Read upcoming sessions into memory
            using (var cmd = new NpgsqlCommand(@"
                SELECT ts.SessionID, ts.CertificateName, ts.PlannedDate
                FROM TrainingSessions ts
                WHERE ts.PlannedDate::date <= CURRENT_DATE + INTERVAL '7 days'
                  AND ts.LastNotifiedDate IS NULL;", conn))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        pendingSessions.Add((
                            reader.GetInt64(0),
                            reader.GetString(1),
                            DateTime.Parse(reader.GetString(2)) // Back to parsing strings
                        ));
                    }
                }
            }

            // 2. Loop through the memory list
            foreach (var session in pendingSessions)
            {
                var participants = new List<(string ParticipantEmail, string ManagerEmail)>();

                // Fetch participants for this specific session
                using (var partCmd = new NpgsqlCommand(@"
                    SELECT u.Email AS ParticipantEmail,
                           mu.Email AS ManagerEmail
                    FROM TrainingParticipants tp
                    JOIN Employees e ON tp.EmployeeID = e.EmployeeID
                    JOIN Users u ON e.EmployeeID = u.EmployeeID
                    LEFT JOIN Groups g ON g.GroupID IN (
                        SELECT gm.GroupID 
                        FROM GroupMembers gm 
                        WHERE gm.EmployeeID = e.EmployeeID
                    )
                    LEFT JOIN Users mu ON mu.EmployeeID = g.ManagerID
                    WHERE tp.SessionID = @SessionID;", conn))
                {
                    partCmd.Parameters.AddWithValue("@SessionID", session.SessionId);

                    using (var partReader = partCmd.ExecuteReader())
                    {
                        while (partReader.Read())
                        {
                            string participantEmail = partReader.IsDBNull(0) ? null : partReader.GetString(0).Trim();
                            string managerEmail = partReader.IsDBNull(1) ? null : partReader.GetString(1).Trim();

                            if (!string.IsNullOrWhiteSpace(participantEmail))
                                participants.Add((participantEmail, managerEmail));
                        }
                    }
                }

                var sentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool allEmailsSent = true;

                foreach (var (participantEmail, managerEmail) in participants)
                {
                    if (sentEmails.Contains(participantEmail)) continue;
                    sentEmails.Add(participantEmail);

                    var ccList = new List<string>();
                    if (!string.IsNullOrWhiteSpace(managerEmail) &&
                        !string.Equals(managerEmail, participantEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        ccList.Add(managerEmail);
                    }

                    bool emailSent = SendSessionReminderEmail(participantEmail, ccList, session.SessionName, session.PlannedDate, smtpHost, smtpPort, smtpUser, smtpPass);

                    if (emailSent)
                    {
                        Log($"Training session reminder sent for {session.SessionName} ({session.PlannedDate:yyyy-MM-dd}) to {participantEmail}" +
                            (ccList.Count > 0 ? $" with CC: {string.Join(",", ccList)}" : ""));
                    }
                    else
                    {
                        allEmailsSent = false;
                        Log($"Failed to send training session reminder for {session.SessionName} to {participantEmail}");
                    }
                }

                // 3. Mark the session as notified
                if (participants.Count > 0 && allEmailsSent)
                {
                    using (var updateCmd = new NpgsqlCommand(@"
                        UPDATE TrainingSessions
                        SET LastNotifiedDate = CURRENT_DATE::text
                        WHERE SessionID = @SessionID;", conn))
                    {
                        updateCmd.Parameters.AddWithValue("@SessionID", session.SessionId);
                        updateCmd.ExecuteNonQuery();
                    }
                }
            }
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

        static bool SendSessionReminderEmail(string toEmail, List<string> ccEmails, string sessionName, DateTime plannedDate,
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
                        Subject = $"Upcoming Training Session: {sessionName}",
                        Body = $"The training session \"{sessionName}\" is scheduled for {plannedDate:yyyy-MM-dd}. " +
                               $"Please ensure all preparations are completed in advance.",
                        IsBodyHtml = false
                    };

                    mail.To.Add(toEmail);

                    // Add CC recipients if any
                    foreach (var cc in ccEmails.Distinct())  // Distinct to avoid duplicates
                    {
                        if (!string.IsNullOrEmpty(cc) && !string.Equals(cc, toEmail, StringComparison.OrdinalIgnoreCase))
                            mail.CC.Add(cc);
                    }

                    // Optional HTML version
                    string htmlBody = $"<p>The training session <strong>{sessionName}</strong> is scheduled for " +
                                      $"<strong>{plannedDate:yyyy-MM-dd}</strong>. Please ensure all preparations are complete.</p>";
                    var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html");
                    mail.AlternateViews.Add(htmlView);

                    client.Send(mail);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error sending session reminder to {toEmail}: {ex.Message}");
                return false;
            }
        }

        static void Log(string message)
            {
            string logFolder = System.IO.Path.Combine(AppContext.BaseDirectory, "Logs");
            if (!System.IO.Directory.Exists(logFolder))
                    System.IO.Directory.CreateDirectory(logFolder);

                string logFile = System.IO.Path.Combine(logFolder, $"NotificationLog_{DateTime.Now:yyyyMMdd}.txt");
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
                Console.WriteLine(logEntry);
                System.IO.File.AppendAllText(logFile, logEntry + Environment.NewLine);
            }
        }
}



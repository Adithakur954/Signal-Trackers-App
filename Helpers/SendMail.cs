using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.AspNetCore.Http;
using System.IO;
using SignalTracker.Helper;
using SignalTracker.Models; 


namespace SignalTracker
{
    
    public class SendMail
    {        
        private readonly ApplicationDbContext db;
        //CommonFunction cf = null;
        public SendMail(ApplicationDbContext context, IHttpContextAccessor? httpContextAccessor = null)
        {
            db = context;
            // The accessor is kept for backward compatibility with existing call sites,
            // but recipient resolution now uses only the explicit list or configured defaults.
            //cf = new CommonFunction(context, httpContextAccessor);
        }        
        public bool send_mail(string message, string[]? to, string[]? bcc, string subject, byte[]? bt_attachment, string attachment_name)
        {

            bool isSend = false;
            try
            {
            //    to = "baghel3349@gmail.com".Split(',');
            //    bcc = "regulatorydatabase.demo@gmail.com".Split(',');

                var Set_email = db.m_email_setting.Where(a => a.m_Status_ID == 1).FirstOrDefault();
                if (Set_email != null)
                {
                    var recipients = ResolveRecipients(to, Set_email);
                    if (recipients.Length == 0)
                        return false;

                    // Sender stays fixed and always comes from the configured SMTP account.
                    string from = Set_email.UserName;
                    string from_password = Set_email.Password;
                    string str_body = "<html><meta name='viewport' content='width=device-width, initial-scale=1'>" +
                                       "<body><table style='width:100%;border: 0;border-radius: 7px;overflow: hidden;' Cellspacing='0px' Cellpadding='0px'> " +
                                       "<tr><td style='padding:10px;'>" + message + "</td></tr><tr><td style='padding:10px 10px 20px;'>" +
                                       "<b style='color:#16992b;'>Regards,<br/>Assistant Secretary,<br/>Forum of Regulators</b><br/><br/><b>P.S.: This is an automated email. Please do not reply to this email.</b><td><tr></body></html>";

                    SmtpClient smtpClient = new SmtpClient();
                    AlternateView avHtml = AlternateView.CreateAlternateViewFromString
                        (str_body, null, MediaTypeNames.Text.Html);


                    //create the mail message
                    MailMessage mail = new MailMessage();
                    //set the FROM address
                    mail.From = new MailAddress(from, "MouleForecast");
                    //set the RECIPIENTS
                    for (int i = 0; i < recipients.Length; i++)
                    {
                        if (recipients[i] == "")
                            continue;
                        else if (recipients[i] == null)
                            continue;

                        mail.To.Add(recipients[i]);
                    }
                    if (bcc != null)
                    {
                        for (int i = 0; i < bcc.Length; i++)
                        {
                            if (bcc[i] == "")
                                continue;
                            else if (bcc[i] == null)
                                continue;

                            mail.Bcc.Add(bcc[i]);
                        }
                    }                  
                    mail.Subject = subject;
                    mail.AlternateViews.Add(avHtml);
                    if (bt_attachment != null)
                    {
                        mail.Attachments.Add(new Attachment(new MemoryStream(bt_attachment), attachment_name));
                    }

                    smtpClient.Host = Set_email.SMTPServer;//"""relay-hosting.secureserver.net";;
                    smtpClient.Port = Convert.ToInt32(Set_email.SMTPPort);//25;

                    smtpClient.EnableSsl = Set_email.SSLayer;//false;
                    smtpClient.UseDefaultCredentials = false;
                    smtpClient.Credentials = new NetworkCredential(from, from_password);
                    smtpClient.Send(mail);
                    isSend = true;
                }
            }
            catch (Exception ex)
            {
                var writelog = new Writelog(db);                
                writelog.write_exception_log(0, "SendMail", "send_mail", DateTime.Now, ex);
            }
            return isSend;
        }

        [Obsolete("Use send_mail_to_configured_recipients instead. This helper no longer resolves the current logged-in user.")]
        public bool send_mail_to_current_user(string message, string subject, string[]? bcc = null, byte[]? bt_attachment = null, string attachment_name = "")
        {
            return send_mail_to_configured_recipients(message, subject, bcc, bt_attachment, attachment_name);
        }

        public bool send_mail_to_configured_recipients(string message, string subject, string[]? bcc = null, byte[]? bt_attachment = null, string attachment_name = "")
        {
            return send_mail(message, null, bcc, subject, bt_attachment, attachment_name);
        }

        private string[] ResolveRecipients(string[]? to, m_email_setting setEmail)
        {
            var recipients = new List<string>();

            AddRecipientValues(recipients, to);

            if (recipients.Count > 0)
                return recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            AddRecipientValues(recipients, new[] { setEmail.received_email_on });

            return recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void AddRecipientValues(List<string> recipients, IEnumerable<string>? values)
        {
            if (values == null)
                return;

            foreach (var value in values)
            {
                foreach (var email in SplitEmailList(value))
                {
                    if (!string.IsNullOrWhiteSpace(email))
                        recipients.Add(email.Trim());
                }
            }
        }

        private static IEnumerable<string> SplitEmailList(string? values)
        {
            if (string.IsNullOrWhiteSpace(values))
                yield break;

            var parts = values.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    yield return trimmed;
            }
        }
    }
}

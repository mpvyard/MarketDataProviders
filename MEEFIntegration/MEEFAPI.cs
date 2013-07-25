﻿/* Copyright (C) 2013 Fairmat SRL (info@fairmat.com, http://www.fairmat.com/)
 * Author(s): Stefano Angeleri (stefano.angeleri@fairmat.com)
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ICSharpCode.SharpZipLib.Zip;

namespace MEEFIntegration
{
    /// <summary>
    /// Provides a simple interface to the MEEF API.
    /// http://www.meff.es/aspx/Comun/Pagina.aspx?l1=Financiero&f=Ddescarga
    /// http://www.meff.es/aspx/Financiero/DescargaFicheros.aspx?id=esp.
    /// </summary>
    internal static class MEEFAPI
    {
        #region Cached Static Data

        static Dictionary<string, byte[]> downloadedData = new Dictionary<string, byte[]>();

        #endregion Cached Static Data

        /// <summary>
        /// Gets a List of <see cref="MEEFHistoricalQuote"/> containing the requested data.
        /// </summary>
        /// <param name="ticker">
        /// The symbol of the ticker to look for quotes.
        /// </param>
        /// <param name="startDate">
        /// The start date to look for quotes, the date is inclusive.
        /// </param>
        /// <param name="endDate">
        /// The ending date to look for quotes, the date is inclusive.
        /// </param>
        /// <returns>
        /// A list of <see cref="MEEFHistoricalQuote"/> containing all
        /// the market open days from startDate to endDate.
        /// The list can be empty if the requested filters yield no results.
        /// </returns>
        /// <exception cref="Exception">
        /// A generic Exception can be thrown in case there are problems
        /// contacting MEEF servers, like timeout or HTTP errors.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// An InvalidDataException might be parsed if the CSV
        /// has different fields than expected.
        /// </exception>
        internal static List<MEEFHistoricalQuote> GetHistoricalQuotes(string ticker, DateTime startDate, DateTime endDate)
        {
            bool actions = true;
            List<MEEFHistoricalQuote> quotes = new List<MEEFHistoricalQuote>();

            // Scan through months and years in order to gather all needed data.
            for (int year = startDate.Year; year <= endDate.Year; year++)
            {
                // Calculate the start and end month for this year depending on the requested range.
                // Depending on the actual year this might change (caused by different distribution
                // of data by the Market Data Provider.
                if (year <= 1997 || (year >= 1999 && year <= 2000))
                {
                    // Single file for the whole year in this case,
                    // So we do a single query even with the first month of the year.
                    GetMonthData(ticker, startDate, endDate, year, 1, ref quotes, true, actions);
                }
                else if (year == 1998 || (year >= 2001 && year <= 2006))
                {
                    // In year 1998 and between 2001 and 2006 the data was split in semesters.
                    int startMonth = (year == startDate.Year) ? startDate.Month : 1;
                    int endMonth = (year == endDate.Year) ? endDate.Month : 12;
                    for (int month = startMonth; month <= endMonth; month += 6)
                    {
                        GetMonthData(ticker, startDate, endDate, year, month, ref quotes, true, actions);
                    }
                }
                else
                {
                    // The rest of the cases are using the new normal format.
                    int startMonth = (year == startDate.Year) ? startDate.Month : 1;
                    int endMonth = (year == endDate.Year) ? endDate.Month : 12;
                    for (int month = startMonth; month <= endMonth; month++)
                    {
                        GetMonthData(ticker, startDate, endDate, year, month, ref quotes, false, actions);
                    }
                }

                // Check if the data could be gather, and if not retry last year.
                if (quotes.Count == 0)
                {
                    if (actions)
                    {
                        // Try the IBEX instead of actions.
                        actions = false;
                        year--;
                    }
                    else
                    {
                        // Couldn't find anything also in the IBEX, go to next year,
                        // maybe the data doesn't exist at this point in time.
                        // Next step we will retry actions again.
                        actions = true;
                    }
                }
            }

            return quotes;
        }

        /// <summary>
        /// Handles gathering of data for a specific month.
        /// This is used to simplify the GetHistoricalQuotes function.
        /// </summary>
        /// <param name="ticker">
        /// The symbol of the ticker to look for quotes.
        /// </param>
        /// <param name="startDate">
        /// The start date to look for quotes, the date is inclusive.
        /// </param>
        /// <param name="endDate">
        /// The ending date to look for quotes, the date is inclusive.
        /// </param>
        /// <param name="year">The specific year which is being looked now.</param>
        /// <param name="month">The specific month which is being looked now.</param>
        /// <param name="quotes">
        /// A list of <see cref="MEEFHistoricalQuote"/> which will contain all
        /// the market open days from startDate to endDate.
        /// If any data is found it's appended to the list (call this function
        /// with progressive dates only).
        /// <param name="oldFormat">
        /// Whathever to parse the CSV from the server with the older format.
        /// </param>
        /// <exception cref="Exception">
        /// A generic Exception can be thrown in case there are problems
        /// contacting MEEF servers, like timeout or HTTP errors.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// An InvalidDataException might be parsed if the CSV
        /// has different fields than expected.
        /// </exception>
        private static void GetMonthData(string ticker, DateTime startDate, DateTime endDate, int year, int month, ref  List<MEEFHistoricalQuote> quotes, bool oldFormat = false, bool actions = true)
        {
            // Try to do the request starting from the wanted data.
            ZipInputStream reader = MakeRequest(new DateTime(year, month, 1), actions);
            ZipEntry entry;

            // Some files contain more than one file so we need to parse them all.
            while ((entry = reader.GetNextEntry()) != null)
            {
                // Start reading from the stream and unzipping the data.
                byte[] data = new byte[4096];
                int size = reader.Read(data, 0, data.Length);

                // Keeps the current data in a format usable to parse the strings from the CSV.
                string entryCSV = string.Empty;

                // Gather all which is in the stream till there is nothing else to read.
                while (size > 0)
                {
                    // Convert the byte array in a string.
                    entryCSV += Encoding.ASCII.GetString(data, 0, size);

                    // If there is an "\n" it means we have at least one line ready to parse
                    // So continue scanning them till we have no more new lines.
                    while (entryCSV.Contains("\n"))
                    {
                        // Check that the new line isn't at the beginning of the string in that case cut it out.
                        if (entryCSV[0] != '\n')
                        {
                            // As we have a CSV line to parse go through  after cleaning it up.
                            MEEFHistoricalQuote quote = new MEEFHistoricalQuote(entryCSV.Substring(0, entryCSV.IndexOf("\n")).Replace("\r", ""), oldFormat);

                            // Check that the quote which was just parsed is what was asked.
                            if (quote.ContractCode == ticker && quote.SessionDate >= startDate && quote.SessionDate <= endDate)
                            {
                                // In that case add to the results.
                                quotes.Add(quote);
                            }
                        }

                        // Calculate and remove the line which was just parsed and prepare for the next line, if any.
                        int pos = entryCSV.IndexOf("\n") + 1;
                        entryCSV = pos < entryCSV.Length ? entryCSV.Substring(pos, entryCSV.Length - pos) : string.Empty;
                    }

                    // Read another chunk of data.
                    size = reader.Read(data, 0, data.Length);
                }
            }
        }

        /// <summary>
        /// Prepares the request string starting from the provided
        /// variables and handles the request.
        /// </summary>
        /// <param name="ticker">
        /// The symbol of the ticker to look for quotes.
        /// </param>
        /// <param name="startDate">
        /// The start date to look for quotes, the date is inclusive.
        /// </param>
        /// <param name="endDate">
        /// The ending date to look for quotes, the date is inclusive.
        /// </param>
        /// <returns>A <see cref="ZipInputStream"/> ready for reading the request result.</returns>
        private static ZipInputStream MakeRequest(DateTime date, bool actions = false)
        {
            // Generate the request to be sent to MEEF site.
            string year = date.Year.ToString();
            string request;

            if (date.Year > 2006)
            {
                request = string.Format("http://www.meff.es/docs/Ficheros/Descarga/dRV/HP{0}{1:00}{2}.zip", year.Substring(year.Length - 2), date.Month, actions ? "ACO" : "FIE");
            }
            else
            {
                // Data before 2007 is stored incosistently so there is need to do several
                // checks in order to ensure the presence of the data.
                // This code is optimized to the actual structure of data on the site, which 
                // isn't supposed to change for the passed dates.
                if (date.Year < 1993)
                {
                    // No data available before 1993
                    throw new Exception("Data is only available from year 1993 " +
                                        "when using this Market Data Provider");
                }
                else if (date.Year >= 1993 && (date.Year <= 1997 || (date.Year >= 1999 && date.Year <= 2000)))
                {
                    // Beetween 1993 and 1997 and between 1999 and 2000 the data
                    // has only one file and it contains the whole year.
                    request = string.Format("http://www.meff.es/docs/Ficheros/Descarga/dRV/HP{0}000{1}.zip", year.Substring(year.Length - 2), actions ? "a" : "i");
                }
                else
                {
                    // The rest of the data follows a semester subdivision. 1s for the first semester 00 for the rest.
                    request = string.Format("http://www.meff.es/docs/Ficheros/Descarga/dRV/HP{0}{1}0{2}.zip", year.Substring(year.Length - 2), date.Month <= 6 ? "1s" : "00", actions ? "a" : "i");
                }
            }

            return MakeRequest(request);
        }

        /// <summary>
        /// Actually makes a request with the provided requestUrl and generates
        /// a <see cref="ZipInputStream"/> ready for reading the result of the request.
        /// </summary>
        /// <param name="requestUrl">The url to use to request data.</param>
        /// <returns>A <see cref="ZipInputStream"/> ready for reading the request result.</returns>
        private static ZipInputStream MakeRequest(string requestUrl)
        {
            Console.WriteLine("request: " + requestUrl);

            // First try fetching the data from the cache.
            if (downloadedData.ContainsKey(requestUrl))
            {
                Console.WriteLine(requestUrl + " was found in cache");
                return new ZipInputStream(new MemoryStream(downloadedData[requestUrl]));
            }

            // Else fetch it from the web server.
            try
            {
                // Prepare the object to handle the request to the MEEF servers.
                HttpWebRequest request = WebRequest.Create(requestUrl) as HttpWebRequest;

                // Actually attempt the request to meef.
                using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
                {
                    // If this point is reached the response is instanced with something.
                    // Check if it was successful.
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new Exception(string.Format("Server error (HTTP {0}: {1}).",
                                                           response.StatusCode,
                                                           response.StatusDescription));
                    }

                    // Obtain the stream of the response and initialize a reader.
                    using (Stream receiveStream = response.GetResponseStream())
                    {
                        MemoryStream ms = new MemoryStream();
                        byte[] buffer = new byte[16 * 4096];
                        int read;
                        while ((read = receiveStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ms.Write(buffer, 0, read);
                        }

                        // Make a copy of the stream for later use
                        downloadedData.Add(requestUrl, ms.ToArray());

                        // Rollback the memory buffer to begin.
                        ms.Seek(0, SeekOrigin.Begin);

                        // Prepare a zip input stream as we are getting a zip file and we need the content of it.
                        return new ZipInputStream(ms);
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception("There was an error while attempting " +
                                    "to contact MEEF servers: " + e.Message);
            }
        }
    }
}

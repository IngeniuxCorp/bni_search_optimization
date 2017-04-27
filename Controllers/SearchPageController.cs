using Ingeniux.Search;
using Lucene.Net.Search;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Configuration;
using Ingeniux.Runtime.Models;

namespace Ingeniux.Runtime.Controllers
{
    /// <summary>
    /// This is a sample controller for search results. 
    /// The purpose of this controller is to allow site developers to specify custom queries and facet logics.
    /// It uses the latest API method for QueryFinal that accept "SearchInstruction" object as parameter to
    /// description customized query for search.
    /// Please do not use this controller, unless you have to manipulate the query.
    /// Stick to DEX components when possible.
    /// </summary>
    /// <remarks>
    /// The controller assumes the search results page schema name is "SearchResults". It is a controller 
    /// just for this schema. It will also look for Views/SearchResults/SearchResults.cshtml as the specific view
    /// location.
    /// It is recommended to write schema specific custom controllers and views.
    /// IMPORTANT: This controller MUST be renamed before included in the project. This is the only way to prevent it from
    /// being overwritten during upgrade.
    /// Therefore, The schema name for the search result page shouldn't be actually called SearchResults.
    /// </remarks>
    public class SearchPageController : CMSPageDefaultController
    {
        /// <summary>
        /// This is the only method that need to overriding.
        /// The goal of override is to set the search results object as .Tag of the pageRequest.
        /// </summary>
        /// <param name="pageRequest"></param>
        /// <returns></returns>
        internal override ActionResult handleStandardCMSPageRequest(CMSPageRequest pageRequest)
        {
            //var redir = checkUserAuth(pageRequest);
            //if (redir != null)
            //{
            //    Response.Redirect(redir);
            //}

            _GetSearchResults(pageRequest);

            return base.handleStandardCMSPageRequest(pageRequest);
        }

        /// <summary>
        /// if login is required, and not logged in, return redirect url
        /// </summary>
        /// <param name="pgRequest"></param>
        /// <returns>redirect url or null</returns>
        private string checkUserAuth(CMSPageRequest pgRequest)
        {
            bool authSucc = (Session["auth.success"] == null) ? false : (bool)Session["auth.success"];
            int authLevel = (int?)Session["auth.accessLevel"] ?? 0;
            var authSet = pgRequest.GetNavigationItems("AncestorNavigation").Where(item => item.GetAttributeValue("Authorization") != "");
            if (authSet.Any())
            {
                string auth = authSet.LastOrDefault().GetAttributeValue("Authorization");
                int requiredAuthLevel = auth.Split('-')[0].ToInt() ?? 0;
                bool accessGranted = (requiredAuthLevel == 0) || (authSucc && authLevel > 0 && authLevel >= requiredAuthLevel);
                if (!accessGranted && !pgRequest.IsPreview && pgRequest.RootElementName != "LoginPage")
                {
                    ICMSElement loginElt = pgRequest.GetLinkItem("Login", true);
                    String loginUrl = (loginElt != null) ? loginElt.URL : "";
                    if (!loginUrl.StartsWith("http")) { loginUrl = Url.Content("~/" + loginUrl); }
                    return loginUrl + "?returnURL=" + Request.Url;
                }
            }

            return null;
        }

        protected void _GetSearchResults(CMSPageRequest pageRequest)
        {
            int pagesize, page;
            bool all;
            Search.SiteSearch siteSearch;
            SearchInstruction instructions = getSearchInstructions(pageRequest,
                out page,
                out pagesize,
                out all,
                out siteSearch);

            //override pagesize from Schema
            var pgSize = pageRequest.GetElementValue("ResultsPageSize");
            if (pgSize != null)
            {
                pagesize = pgSize.ToInt() ?? 10;
            }

            Search.Search search = new Search.Search(Request);

            int count = 0;

            if (search != null)
            {
                //use the SearchInstruction based overload method to achieve max flexibility (non-paginated results)
                var pageResults = search.QueryFinal(siteSearch, out count, instructions, 200, true);

                //find matches for both member records and pages
                var M = new MembersWrapper();
                string termsString = pageRequest.QueryString["terms"] ?? "";
                //var memDic = M.Search(termsString);
                var memDic = new List<KeyValuePair<string, Member>>();
                var pgCollection = pageResults
                    .Select(r => new KeyValuePair<float, SearchResultItem>(r.Score, r));

                //apply sourceFilter and merge the two dictionaries
                string sourceFilter = pageRequest.QueryString["sourceFilter"] ?? "";
                IEnumerable<KeyValuePair<float, object>> mergedCollection = new KeyValuePair<float, object>[0];
                if (sourceFilter == "" || sourceFilter != "members")
                {
                    mergedCollection = pgCollection
                        .Where(x =>
                        {
                            bool externalItem = (x.Value.Type == "ingeniux.search.htmlsitesource");
                            return (sourceFilter == ""
                            || (sourceFilter == "local" && !externalItem)
                            || (sourceFilter == "bni.com" && externalItem));
                        })
                        .Select(x =>
                            new KeyValuePair<float, object>(x.Key, x.Value));
                }

                if (sourceFilter == "" || sourceFilter == "members")
                {
                    mergedCollection = mergedCollection
                        .Concat(memDic
                            .Select(x => new KeyValuePair<float, object>(float.Parse(x.Key.SubstringBefore("_")), x.Value)));
                }

                //sort results
                var returnResults = mergedCollection.OrderByDescending(x => x.Key).Select(x => x.Value).ToList();

                //apply pagination
                ViewBag.totalResults = returnResults.Count;
                ViewBag.pageSize = pagesize;
                pageRequest.Tag = returnResults.Skip((page - 1) * pagesize).Take(pagesize).ToList();
                //pageRequest.Tag = mergedDic;  //no pagination
            }

        }

        /// <summary>
        /// This is the method that will construct the SearchInstruction object.
        /// This is the place to manipulate the SearchInstruction object in order to
        /// generate a query with custom logics
        /// </summary>
        /// <param name="pageRequest">CMS Page object</param>
        /// <param name="page">Page number, default to 1</param>
        /// <param name="pagesize">Page size, default to 10</param>
        /// <param name="all">All records or not</param>
        /// <param name="siteSearch">Persisting SiteSearch object</param>
        /// <returns>Customized search instructions</returns>
        private SearchInstruction getSearchInstructions(CMSPageRequest pageRequest,
            out int page,
            out int pagesize,
            out bool all,
            out Search.SiteSearch siteSearch)
        {
            string[] termsA = pageRequest.QueryString["terms"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    s => s.Split(','))
                .Return(new string[0]);

            //default to not categories filter
            string[] catsA = pageRequest.QueryString["catids"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    s => s.Split(','))
                .Return(new string[0]);

            bool categoryById = pageRequest.QueryString["catsbyid"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    sa => sa.ToBoolean())
                .Return(false);

            //default to no types filter
            string[] typesA = pageRequest.QueryString["types"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    s => s.Split(','))
                .Return(new string[0]);

            string[] localesA = pageRequest.QueryString["locales"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    s => s.Split(','))
                .Return(new string[0]);

            //default to first page instead of all records
            string pageStr = pageRequest.QueryString["page"]
                .ToNullOrEmptyHelper()
                .Return("1");

            //default page size to 10 records
            pagesize = pageRequest.QueryString["pagesize"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    ps => ps.ToInt())
                .Propagate(
                    ps => ps.Value)
                .Return(10);

            string[] sourcesA = pageRequest.QueryString["sources"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    s => s.Split(','))
                .Return(new string[0]);

            string sortby = pageRequest.QueryString["sortby"] ?? string.Empty;
            bool sortAscending = pageRequest.QueryString["sortasc"]
                .ToNullOrEmptyHelper()
                .Propagate(
                    sa => sa.ToBoolean())
                .Return(false);

            all = false;
            page = 0;
            if (!int.TryParse(pageStr, out page))
                all = true;
            else if (page < 1)
                all = true;

            siteSearch = Reference.Reference.SiteSearch;

            //use the hiliter in configuration, or default hiliter with strong tags
            QueryBuilder.CategoryFilterOperator = Search.Search.GetCategoryFilterOperator();

            SearchInstruction instructions = new SearchInstruction(siteSearch.DefaultQueryAnalyzer);
            instructions.AddQuery(instructions.GetFullTextTermQuery(Occur.MUST, true, termsA));
            //instructions.AddQuery(instructions.GetFieldTermQuery(Occur.MUST, "fulltext", true, termsA));

            if (typesA.Length > 0)
                instructions.AddQuery(
                    instructions.GetTypeQuery(Occur.MUST, typesA));

            if (sourcesA.Length > 0)
            {
                instructions.AddQuery(
                    instructions.GetSourceQuery(Occur.MUST, sourcesA));
            }

            if (!string.IsNullOrWhiteSpace(sortby))
            {
                instructions.AddSort(new SortField(sortby, CultureInfo.InvariantCulture,
                    !sortAscending));
            }

            if (localesA.Length > 0)
            {
                instructions.AddQuery(
                    instructions.GetLocaleQuery(Occur.MUST, localesA));
            }

            if (catsA.Length > 0)
                instructions.AddQuery(
                    (!categoryById) ?
                        instructions.GetCategoryQuery(Occur.MUST, QueryBuilder.CategoryFilterOperator,
                            catsA) :
                        instructions.GetCategoryIdQuery(Occur.MUST, QueryBuilder.CategoryFilterOperator,
                            catsA));

            return instructions;
        }
    }
}
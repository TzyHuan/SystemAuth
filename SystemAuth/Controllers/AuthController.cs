﻿using System;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using SystemAuth.ViewModels;
using SystemAuth.Extensions;
using SystemAuth.Models.SQLite;

namespace SystemAuth.Controllers
{
    [Produces("application/json")]
    [Route("api/[controller]")]
    public class AuthController : Controller
    {
        private readonly IConfiguration _config;
        private readonly SystemAuthContext _context;
        private readonly IHttpContextAccessor _accessor;
        
        public AuthController(IConfiguration config, SystemAuthContext context, IHttpContextAccessor accessor)
        {
            _config = config;
            _context = context;
            _accessor = accessor;
        }

        [HttpGet("[action]/{ctrlName}/{actName}")]
        public IActionResult CheckAuth([FromRoute] string ctrlName, [FromRoute]string actName)
        {
            string authHeader = HttpContext.GetToken();
            string userAccount = HttpContext.CurrentUserId();       //從jwt抓id，無jwt或偽造時，讀出userAccount = null
            var userRole = _context.GetUserRoles(userAccount);

            // ----正式驗證request權限---- //
            if (authHeader != null && authHeader.StartsWith("Bearer", true, CultureInfo.CurrentCulture))
            {
                if (_context.HasAllowedAction(userRole, actName) && _context.HasUserRole(userRole, userAccount))
                {
                    return Ok();
                }
            }

            return Unauthorized();
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> Authenticate([FromBody]AuthRequest AuthRequest) //, string Account, string Password
        {
            bool HasUser = _context.FindUser(AuthRequest.Account, AuthRequest.Password);
            bool HasToken = _context.FindToken(AuthRequest.Account);
            
            if (HasUser)
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_config["Jwt:Key"]);
                var authTime = DateTime.UtcNow.ToLocalTime();//ToLocalTime變UTC+8時區
                var expiresAt = authTime.AddDays(7);
                
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new Claim[]
                    {
                        new Claim(JwtClaimTypes.Audience, _config["Jwt:Audience"]),
                        new Claim(JwtClaimTypes.Issuer, _config["Jwt:Issuer"]),
                        new Claim(JwtClaimTypes.Id, AuthRequest.Account),
                        //new Claim(JwtClaimTypes.RoleId, RoleID.ToString()), //停止在jwt加入角色資訊，統一用id(帳號)判斷
                        //new Claim(JwtClaimTypes.Email, user.Email),
                        //new Claim(JwtClaimTypes.PhoneNumber, user.PhoneNumber)
                    }),
                    Expires = expiresAt,
                    NotBefore = authTime,
                    IssuedAt = authTime,
                    SigningCredentials = new SigningCredentials
                    (
                        new SymmetricSecurityKey(key),
                        SecurityAlgorithms.HmacSha256Signature
                    )
                };
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);

                try
                {
                    Token SaveInfo = new Token
                    {
                        Account = AuthRequest.Account,
                        TokenCode = tokenString,
                        AuthTime = authTime,
                        ExpiredTime = expiresAt,
                        Ip = _accessor.HttpContext.Connection.RemoteIpAddress.ToString()
                    };
                    //將Token資訊加入Database
                    if (HasToken)
                    {
                        //若過去已有建立過Token，刷新資料
                        Token existInfo = _context.Token.Where(x => x.Account == AuthRequest.Account).FirstOrDefault();
                        _context.Entry(existInfo).State = EntityState.Modified;
                        _context.Entry(existInfo).CurrentValues.SetValues(SaveInfo);
                    }
                    else
                    {
                        _context.Token.Add(SaveInfo);
                    } 

                    await _context.SaveChangesAsync();
                }
                catch(Exception ex)
                {                    
                    return BadRequest("Could not create token \n" + ex);
                }               

                return Ok(new
                {
                    access_token = tokenString,
                    token_type = "Bearer",
                    profile = new
                    {
                        //sid = user.Id,
                        //name = user.Name,
                        auth_time = new DateTimeOffset(authTime).ToUnixTimeSeconds(),
                        expires_at = new DateTimeOffset(expiresAt).ToUnixTimeSeconds()
                    }
                });
            }
            else
            {
                //紀錄System Log
                _context.SystemLog.Add(new SystemLog
                {
                    LogTime = DateTime.Now,
                    Account = AuthRequest.Account,
                    Action = ControllerContext.ActionDescriptor.ActionName,
                    Detail = "Failure to authorize ",
                    Ip = _accessor.HttpContext.Connection.RemoteIpAddress.ToString()
                });
                await _context.SaveChangesAsync();
                return Unauthorized();
            }            
        }
    }

    public static class Extension
    {
        public static bool FindUser(this SystemAuthContext context, string Account, string Password)
        {
            //用string.Compare區分大小寫判斷帳號密碼
            //若單純用x.Account == Account && x.Password == Password，無法區分大小寫
            return context.Member.Any(x =>
                x.Account ==  Account &&
                x.Password == Password
            );
        }

        public static bool FindToken(this SystemAuthContext context, string Account)
        {
            //用string.Compare區分大小寫判斷帳號密碼
            //若單純用x.Account == Account && x.Password == Password，無法區分大小寫
            return context.Token.Any(x =>
                x.Account == Account
            );
        }

    }
    
}
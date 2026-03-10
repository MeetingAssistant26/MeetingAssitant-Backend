
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Authentication.Authentication
{
    public class JwtProvider(IOptions<JwtOptions> jwtOptions) : IJwtProvider
    {
        private readonly JwtOptions _jwtOptions = jwtOptions.Value;

        public (string token, int expiresIn) GenerateJwtToken(ApplicationUser User,IEnumerable<string>roles,IEnumerable<string>permissions)
        {
            Claim[] claims = new Claim[] {
                new(JwtRegisteredClaimNames.Sub,User.Id),
                new(JwtRegisteredClaimNames.Email,User.Email!),
                new(JwtRegisteredClaimNames.GivenName,User.FirstName!),
                new(JwtRegisteredClaimNames.FamilyName,User.LastName!),
                new(JwtRegisteredClaimNames.Jti,Guid.NewGuid().ToString()),
                new(nameof(roles),JsonSerializer.Serialize(roles),JsonClaimValueTypes.JsonArray),
                new(nameof(permissions),JsonSerializer.Serialize(permissions),JsonClaimValueTypes.JsonArray),


                };

            var symmetricSecurityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));

            var signingCredentials=new SigningCredentials(symmetricSecurityKey,SecurityAlgorithms.HmacSha256);

            var token=new JwtSecurityToken(
                issuer: _jwtOptions.Issuer,
                audience: _jwtOptions.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_jwtOptions.expiresIn),
                signingCredentials: signingCredentials
                );


            return(token:new JwtSecurityTokenHandler().WriteToken(token),_jwtOptions.expiresIn);


        }

        public string? validateJwtToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            var symmetricSecurityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
            try
            {
                tokenHandler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = symmetricSecurityKey,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                }, out SecurityToken validatedToken);

                var jwtToken = (JwtSecurityToken)validatedToken;
                var userId = jwtToken.Claims.First(x => x.Type == JwtRegisteredClaimNames.Sub).Value;
                return userId;
            }
            catch
            {
                return null;
            }

        }
    }
}
